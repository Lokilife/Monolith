// (c) Space Exodus Team - EXDS-RL with CLA

using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Content.Server.SS220.Extensions;
using Content.Shared.CCVar;
using Content.Shared.SS220.CCVars;
using Content.Shared.SS220.EPA;
using Robust.Shared.Network;

namespace Content.Server.SS220.EPA;

public sealed partial class EPAManager
{
    private AuthMode _authMode; // not necessary, huh?
    private EPAMode _epaMode;

    // (PeerConnectionId, TCS)
    private readonly Dictionary<long, EPAHandshakeState> _handshakes = new();

    /// <inheritdoc />
    public event Func<INetChannel, Task>? AuthFinished;

    private void InitializeAuth()
    {
        _config.OnValueChanged(CCVars.AuthMode, val => _authMode = (AuthMode)val, true);
        _config.OnValueChanged(CCVars220.EPAMode, val => _epaMode = (EPAMode)val, true);

        _net.InitialHandshakeComplete += OnHandshake;
        _net.Disconnect += OnDisconnect;

        _net.RegisterNetMessage<MsgEPALogin>(OnLogin, accept: NetMessageAccept.Server | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPACheckSession>(OnSessionCheck, accept: NetMessageAccept.Server | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPACreateSession>(OnCreateSession, accept: NetMessageAccept.Server | NetMessageAccept.Handshake);

        _net.RegisterNetMessage<MsgEPAHello>(accept: NetMessageAccept.Client | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPAReject>(accept: NetMessageAccept.Client | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPAAccept>(accept: NetMessageAccept.Client | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPACreateSessionRes>(accept: NetMessageAccept.Client | NetMessageAccept.Handshake);
        _net.RegisterNetMessage<MsgEPANewSession>(accept: NetMessageAccept.Client | NetMessageAccept.Handshake);
    }

    private Task OnHandshake(NetChannelArgs args)
    {
        var msg = new MsgEPAHello
        {
            ShouldAuth = _epaMode != EPAMode.Disabled
        };
        args.Channel.SendMessage(msg);

        if (_epaMode == EPAMode.Disabled)
            return Task.CompletedTask;

        _sawmill.Debug($"Paused handshake for {args.Channel.ToPrettyString()} and waiting next steps");

        var tcs = new TaskCompletionSource();

        var state = new EPAHandshakeState
        {
            Channel = args.Channel,
            Tcs = tcs
        };
        _handshakes.Add(args.Channel.ConnectionId, state);

        return tcs.Task;
    }

    private void OnDisconnect(object? sender, NetDisconnectedArgs args)
    {
        _sawmill.Debug($"{args.Channel.ToPrettyString()} disconnected during handshake");
        CancelHandshake(args.Channel);
    }

    private void OnLogin(MsgEPALogin msg)
    {
        Task.Run(async () =>
        {
            if (_epaMode == EPAMode.Disabled)
            {
                if (AuthFinished != null)
                    await AuthFinished.Invoke(msg.MsgChannel);

                return;
            }

            if (!TryReadToken(msg.MsgChannel, msg.Token, out var payload))
            {
                _sawmill.Debug($"{msg.MsgChannel.ToPrettyString()} token rejected, token was: {msg.Token}");

                var rejectMsg = new MsgEPAReject
                {
                    Reason = "epa-token-invalid-message"
                };

                msg.MsgChannel.SendMessage(rejectMsg);

                return;
            }

            var channel = msg.MsgChannel;

            if (_epaMode == EPAMode.Authorization)
            {
                var guid = payload.UserId;
                var userId = new NetUserId(guid);
                var newData = new NetUserData(userId, payload.Username)
                {
                    CreatedTime = channel.UserData.CreatedTime,
                    HWId = channel.UserData.HWId,
                    ModernHWIds = channel.UserData.ModernHWIds,
                    // PatronTier
                    // Trust
                };
                _sawmill.Debug("Performing net channel re-setup");
                _net.ReSetupChannel(msg.MsgChannel, newData);
            }

            if (_epaMode == EPAMode.Validation && _authMode == AuthMode.Required
                && channel.AuthType == LoginType.LoggedIn)
            {
                // temporal mechanism for soft migration of players to full EPA auth
                var token = await SignToken(channel.UserId);
                var newSession = new MsgEPANewSession()
                {
                    Token = token,
                };
                channel.SendMessage(newSession);
            }

            if (AuthFinished != null)
                await AuthFinished.Invoke(msg.MsgChannel);

            ReleaseHandshake(channel);

            var acceptMsg = new MsgEPAAccept()
            {
                UserId = channel.UserId,
                Username = channel.UserName,
            };
            channel.SendMessage(acceptMsg);
        });
    }

    private void CancelHandshake(INetChannel channel)
    {
        if (TryGetHandshakeState(channel, out var state))
        {
            _handshakes.Remove(channel.ConnectionId);
            state.Tcs.SetCanceled();
        }
    }

    private void ReleaseHandshake(INetChannel channel)
    {
        if (TryGetHandshakeState(channel, out var state))
        {
            _handshakes.Remove(channel.ConnectionId);
            state.Tcs.SetResult();
        }
    }

    private void OnCreateSession(MsgEPACreateSession msg)
    {
        Task.Run(async () =>
        {
            var (authUrl, code) = await CreateSession();

            if (TryGetHandshakeState(msg.MsgChannel, out var state))
            {
                state.SessionCode = code;
            }

            var res = new MsgEPACreateSessionRes()
            {
                AuthUrl = authUrl,
            };
            msg.MsgChannel.SendMessage(res);
        });
    }

    private void OnSessionCheck(MsgEPACheckSession msg)
    {
        Task.Run(async () =>
        {
            if (TryGetHandshakeState(msg.MsgChannel, out var state))
            {
                if (state.SessionCode == null)
                {
                    _sawmill.Warning($"{msg.MsgChannel.ToPrettyString()} asked to validate session without a session code");
                    return;
                }

                var token = await CheckSessionCode(state.SessionCode);

                if (token == null)
                    return;

                var res = new MsgEPANewSession()
                {
                    Token = token
                };
                msg.MsgChannel.SendMessage(res);
            }
        });
    }

    private bool TryGetHandshakeState(INetChannel channel, [NotNullWhen(true)] out EPAHandshakeState? state)
    {
        state = null;

        if (_handshakes.TryGetValue(channel.ConnectionId, out var fetched)) // why do TryGetValue returns non-nullable result?
        {
            state = fetched;
            return true;
        }

        return false;
    }

    private async Task<(string AuthUrl, string SessionCode)> CreateSession()
    {
        // TODO: use real API call to get new session code
        return ("https://auth0.ss220.club/insert-your-code-here", "insert-your-code-here");
    }

    private async Task<string> SignToken(Guid uuid)
    {
        // TODO: real API request

        // debug token, see CheckSessionCode for details
        return "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5NTczZWE0MC1lZWQyLTRjM2QtYTAzOC05OGM0YmVlYTZlZTEiLCJ1c2VybmFtZSI6Ikxva2lsaWZlVEMiLCJhdWQiOiJodHRwczovL2dhbWUuc3MyMjAuY2x1Yi8iLCJpc3MiOiJodHRwczovL2F1dGgwLnNzMjIwLmNsdWIvIiwiaXBzIjpbIjEyNy4wLjAuMSIsIjAuMC4wLjAiXSwiaWF0IjoxNzgxMTg3MjQ4LCJleHAiOjE3ODE4Njc2NDJ9.eVjPufNob_4LqzIOYerZp7YF2n6bqGP3nIb5E3oBeLsv5xOaoyWKWJgeuKIzVbswMJVncl5KXJ3ySWFpbysDsw";
    }

    private async Task<string?> CheckSessionCode(string code)
    {
        // TODO: real API code check

        // this is my manually created token for debug purposes with ES256 which is valid till June 19th, 2026
        // {
        //   "sub": "9573ea40-eed2-4c3d-a038-98c4beea6ee1",
        //   "username": "LokilifeTC",
        //   "aud": "https://game.ss220.club/",
        //   "iss": "https://auth0.ss220.club/",
        //   "ips": [
        //     "127.0.0.1",
        //     "0.0.0.0"
        //   ],
        //   "iat": 1781187248,
        //   "exp": 1781867642
        // }
        // here is my public DER key to validate it:
        // jwt_key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkwtjMnygN/Fs3DUdFb4RM+9GqQJL2A2KXlGHI1iTVMPFPSOrjLmS8u/n+3hTSjIo8M1Rn2lBucQChqlSI+bCnw=="
        // just insert it to [epa] section in server_config.toml

        // or you can try to create your own token with your own signature on https://jwt.io
        // here is one-line command to generate ES256 keys in all needed formats in current working dir:
        /*
        openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out private-key.pem && \
            openssl pkey -in private-key.pem -pubout -out public-key.pem && \
            openssl pkey -in private-key.pem -outform DER | openssl base64 -e -A > private-key-der.base64 && \
            openssl pkey -in private-key.pem -pubout -outform DER | openssl base64 -e -A > public-key-der.base64
        */

        return "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5NTczZWE0MC1lZWQyLTRjM2QtYTAzOC05OGM0YmVlYTZlZTEiLCJ1c2VybmFtZSI6Ikxva2lsaWZlVEMiLCJhdWQiOiJodHRwczovL2dhbWUuc3MyMjAuY2x1Yi8iLCJpc3MiOiJodHRwczovL2F1dGgwLnNzMjIwLmNsdWIvIiwiaXBzIjpbIjEyNy4wLjAuMSIsIjAuMC4wLjAiXSwiaWF0IjoxNzgxMTg3MjQ4LCJleHAiOjE3ODE4Njc2NDJ9.eVjPufNob_4LqzIOYerZp7YF2n6bqGP3nIb5E3oBeLsv5xOaoyWKWJgeuKIzVbswMJVncl5KXJ3ySWFpbysDsw";
    }
}
