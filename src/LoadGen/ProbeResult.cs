namespace SevenDTD.LoadGen;

public sealed class ProbeResult
{
    public required bool Pass { get; init; }
    public required HashSet<string> Stages { get; init; }
    public required bool Connected { get; init; }
    public string? DisconnectReason { get; init; }
    public required List<string> Lines { get; init; }
    public long ElapsedMs { get; init; }
}
