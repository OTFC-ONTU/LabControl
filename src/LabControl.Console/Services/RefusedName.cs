namespace LabControl.Console.Services;

/// <summary>A name the PCs' whitelists refused lately (M6 portion 3, D-74), and on how many linked PCs.</summary>
public sealed record RefusedName(string Name, int PcCount);
