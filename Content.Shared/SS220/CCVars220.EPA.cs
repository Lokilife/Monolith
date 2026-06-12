// (c) Space Exodus Team - EXDS-RL with CLA

using Robust.Shared.Configuration;

namespace Content.Shared.SS220.CCVars;

public sealed partial class CCVars220
{
    /// <summary>
    /// Mode for <see cref="EPA.IEPAManager"/>, see <see cref="EPA.EPAMode"/>
    /// </summary>
    public static readonly CVarDef<int> EPAMode =
        CVarDef.Create("epa.mode", 0, CVar.SERVER | CVar.REPLICATED); // TODO: DONT FORGET TO CHANGE TO ZERO

    /// <summary>
    /// Base64 encoded JWT key used for validation
    /// </summary>
    public static readonly CVarDef<string> EPAJWTKey =
        CVarDef.Create("epa.jwt_key", "", CVar.SERVERONLY);

    /// <summary>
    /// Path to PEM-encoded file with JWT key used for validation
    /// </summary>
    public static readonly CVarDef<string> EPAJWTKeyPemPath =
        CVarDef.Create("epa.jwt_key_pem", "", CVar.SERVERONLY);

    /// <summary>
    /// EPA JWT issuer
    /// </summary>
    public static readonly CVarDef<string> EPAJWTIssuer =
        CVarDef.Create("epa.jwt_iss", "", CVar.SERVERONLY);

    /// <summary>
    /// EPA JWT audience
    /// </summary>
    public static readonly CVarDef<string> EPAJWTAudience =
        CVarDef.Create("epa.jwt_aud", "", CVar.SERVERONLY);

    /// <summary>
    /// How much clock skew is allowed in seconds, default is 5 minutes
    /// </summary>
    public static readonly CVarDef<int> EPAJWTClockSkew =
        CVarDef.Create("epa.jwt_clock_skew", 300, CVar.SERVERONLY);
}
