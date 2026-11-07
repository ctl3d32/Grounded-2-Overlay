using System.Text.Json.Serialization;

namespace G2Overlay.PatternScanner;

/// <summary>
/// Assinatura de uma instrução com operando RIP-relativo que referencia uma variável global.
/// Endereço resolvido = endereço_do_match + InstructionEnd + int32(match + DisplacementOffset).
/// </summary>
public sealed class Signature
{
    /// <summary>Global alvo: "GWorld", "GNames" ou "GObjects".</summary>
    public string Target { get; set; } = "";

    /// <summary>Padrão AOB estilo IDA.</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Posição, dentro do padrão, do deslocamento de 32 bits (disp32).</summary>
    public int DisplacementOffset { get; set; }

    /// <summary>Posição, dentro do padrão, do fim da instrução que contém o disp32 (base do RIP).</summary>
    public int InstructionEnd { get; set; }

    /// <summary>Instrução desmontada (documentação).</summary>
    public string Disassembly { get; set; } = "";

    /// <summary>RVA onde a assinatura foi encontrada na build analisada (documentação).</summary>
    public string FoundAtRva { get; set; } = "";

    [JsonIgnore]
    public Pattern? Compiled { get; set; }

    public override string ToString() => $"{Target}: {Pattern}";
}

/// <summary>Arquivo Config/signatures.json.</summary>
public sealed class SignatureFile
{
    public string GameBuild { get; set; } = "";
    public List<Signature> Signatures { get; set; } = new();
}
