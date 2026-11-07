namespace G2Overlay.PatternScanner;

/// <summary>
/// Assinaturas descobertas por análise estática do Grounded2Steam-Win64-Shipping.exe
/// (build "++Augusta+release-0.6.0.3", FileVersion Augusta-CL-3207493, UE 5.7).
/// Cada uma foi verificada: ocorrência única na seção .text e resolve exatamente para o
/// endereço da global relatado pelo dump (GWorld RVA 0xA6C76E0, GNames RVA 0xA499E40, GObjects RVA 0xA53BC20).
/// Usadas quando Config/signatures.json não existe ou está inválido.
/// </summary>
public static class DefaultSignatures
{
    public const string Build = "Augusta-CL-3207493 (++Augusta+release-0.6.0.3) / UE 5.7";

    public static List<Signature> Create() => new()
    {
        // ------------------------------------------------------------------ GWorld (UWorld*)
        new() { Target = "GWorld", Pattern = "48 89 05 ?? ?? ?? ?? 0F 28 D6", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "mov [rip+GWorld], rax ; movaps xmm2, xmm6", FoundAtRva = "0x39FB279" },
        new() { Target = "GWorld", Pattern = "48 89 0D ?? ?? ?? ?? 48 85 F6", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "mov [rip+GWorld], rcx ; test rsi, rsi", FoundAtRva = "0x39FB4C8" },
        new() { Target = "GWorld", Pattern = "75 07 48 8B 05 ?? ?? ?? ?? C3", DisplacementOffset = 5, InstructionEnd = 9,
                Disassembly = "jne +7 ; mov rax, [rip+GWorld] ; ret", FoundAtRva = "0x34070C0" },
        new() { Target = "GWorld", Pattern = "48 89 05 ?? ?? ?? ?? 4C 8B 76 78", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "mov [rip+GWorld], rax ; mov r14, [rsi+78h]", FoundAtRva = "0x40F53DB" },

        // ------------------------------------------------------------------ GNames (FNamePool)
        new() { Target = "GNames", Pattern = "48 8D 0D ?? ?? ?? ?? 8B FA 75 0F", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "lea rcx, [rip+NamePool] ; mov edi, edx ; jne", FoundAtRva = "0x12ECFE4" },
        new() { Target = "GNames", Pattern = "48 8D 05 ?? ?? ?? ?? 48 8B D1 48 8B C8", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "lea rax, [rip+NamePool] ; mov rdx, rcx ; mov rcx, rax", FoundAtRva = "0x12ED102" },
        new() { Target = "GNames", Pattern = "48 8B D9 74 09 4C 8D 05 ?? ?? ?? ?? EB 16", DisplacementOffset = 8, InstructionEnd = 12,
                Disassembly = "mov rbx, rcx ; je +9 ; lea r8, [rip+NamePool] ; jmp +16h", FoundAtRva = "0x12EF76F" },

        // ------------------------------------------------------------------ GObjects (FUObjectArray)
        new() { Target = "GObjects", Pattern = "48 8D 0D ?? ?? ?? ?? 48 8B 04 F7", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "lea rcx, [rip+GUObjectArray] ; mov rax, [rdi+rsi*8]", FoundAtRva = "0x140C4EA" },
        new() { Target = "GObjects", Pattern = "44 8B 0D ?? ?? ?? ?? 48 8B CE", DisplacementOffset = 3, InstructionEnd = 7,
                Disassembly = "mov r9d, [rip+GUObjectArray] ; mov rcx, rsi", FoundAtRva = "0x1415EC3" },
        new() { Target = "GObjects", Pattern = "8B 0D ?? ?? ?? ?? 48 8D 4D 88", DisplacementOffset = 2, InstructionEnd = 6,
                Disassembly = "mov ecx, [rip+GUObjectArray] ; lea rcx, [rbp-78h]", FoundAtRva = "0x14182F6" },
    };
}
