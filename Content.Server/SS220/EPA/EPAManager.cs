// (c) Space Exodus Team - EXDS-RL with CLA

using System.Threading.Tasks;
using Content.Shared.SS220.EPA;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server.SS220.EPA;

public sealed class EPAHandshakeState
{
    public required TaskCompletionSource Tcs;
    public required INetChannel Channel;
    public string? SessionCode;
}

public sealed partial class EPAManager : IServerEPAManager
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerNetManager _net = default!;
    [Dependency] private ILogManager _log = default!;

    private ISawmill _sawmill = default!;

    public void Initialize()
    {
        _sawmill = _log.GetSawmill("epa");

        InitializeAuth();
        InitializeJWT();
    }
}
