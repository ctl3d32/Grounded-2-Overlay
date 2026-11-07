# AOBs e assinaturas

Todas as assinaturas abaixo foram obtidas por **análise estática** do `Grounded2Steam-Win64-Shipping.exe`
(183 MB, PE64 sem empacotamento, `.text` = 0x7A8E4F1 bytes) desta forma:

1. As RVAs das globais vieram do dump (GWorld `0xA6C76E0`, GNames `0xA499E40`, GObjects `0xA53BC20`).
2. Varredura de toda a `.text` em busca de instruções com operando RIP-relativo apontando para cada global
   (39 referências a GWorld, 41 a GNames, 105 a GObjects).
3. Desmontagem (objdump, sintaxe Intel) de cada referência e geração de padrões com curingas em
   **todos** os deslocamentos voláteis: o próprio disp32 da global, `call/jmp rel32` e `jcc rel32`.
4. Expansão instrução a instrução até o padrão ficar **único** na `.text`.
5. Verificação final: cada padrão tem exatamente 1 ocorrência e resolve exatamente para a RVA do dump (10/10 OK).

Fórmula de resolução (todas são RIP-relativas):

```
match   = endereço onde o padrão foi encontrado
disp32  = *(int32*)(match + DisplacementOffset)
global  = match + InstructionEnd + disp32
```

O overlay testa as assinaturas de cada global em ordem, ignora as ausentes/ambíguas, compara os resultados
entre si (log de discordância) e **valida semanticamente** o endereço (ver OFFSETS.md) antes de usá-lo.
Resultado validado é guardado em `Config/scan_cache.json`, com chave `TimeDateStamp-SizeOfImage` do PE;
uma atualização do jogo muda a chave e força nova varredura.

## Lista

| # | Global | Padrão (AOB) | disp | fim instr. | RVA do match | Instruções |
|---|---|---|---|---|---|---|
| 1 | GWorld | `48 89 05 ?? ?? ?? ?? 0F 28 D6` | 3 | 7 | `0x39FB279` | `mov [rip+GWorld], rax ; movaps xmm2, xmm6` |
| 2 | GWorld | `48 89 0D ?? ?? ?? ?? 48 85 F6` | 3 | 7 | `0x39FB4C8` | `mov [rip+GWorld], rcx ; test rsi, rsi` |
| 3 | GWorld | `75 07 48 8B 05 ?? ?? ?? ?? C3` | 5 | 9 | `0x34070C0` | `jne +7 ; mov rax, [rip+GWorld] ; ret` |
| 4 | GWorld | `48 89 05 ?? ?? ?? ?? 4C 8B 76 78` | 3 | 7 | `0x40F53DB` | `mov [rip+GWorld], rax ; mov r14, [rsi+78h]` |
| 5 | GNames | `48 8D 0D ?? ?? ?? ?? 8B FA 75 0F` | 3 | 7 | `0x12ECFE4` | `lea rcx, [rip+NamePool] ; mov edi, edx ; jne` |
| 6 | GNames | `48 8D 05 ?? ?? ?? ?? 48 8B D1 48 8B C8` | 3 | 7 | `0x12ED102` | `lea rax, [rip+NamePool] ; mov rdx, rcx ; mov rcx, rax` |
| 7 | GNames | `48 8B D9 74 09 4C 8D 05 ?? ?? ?? ?? EB 16` | 8 | 12 | `0x12EF76F` | `mov rbx, rcx ; je +9 ; lea r8, [rip+NamePool] ; jmp +16h` |
| 8 | GObjects | `48 8D 0D ?? ?? ?? ?? 48 8B 04 F7` | 3 | 7 | `0x140C4EA` | `lea rcx, [rip+GUObjectArray] ; mov rax, [rdi+rsi*8]` |
| 9 | GObjects | `44 8B 0D ?? ?? ?? ?? 48 8B CE` | 3 | 7 | `0x1415EC3` | `mov r9d, [rip+GUObjectArray] ; mov rcx, rsi` |
| 10 | GObjects | `8B 0D ?? ?? ?? ?? 48 8D 4D 88` | 2 | 6 | `0x14182F6` | `mov ecx, [rip+GUObjectArray] ; lea rcx, [rbp-78h]` |

## Assinaturas de reserva (não usadas por padrão)

Também únicas na build atual, mas mais curtas/frágeis. Podem ser adicionadas a `signatures.json` se as principais falharem:

| Global | Padrão | disp | fim |
|---|---|---|---|
| GNames | `8B 19 74 09 48 8D 15 ?? ?? ?? ?? EB 16` | 7 | 11 |
| GNames | `8B D9 74 09 48 8D 15 ?? ?? ?? ?? EB 16` | 7 | 11 |
| GNames | `48 8B F9 74 09 4C 8D 05 ?? ?? ?? ?? EB 16` | 8 | 12 |

## Strings de identificação da build

| Item | Valor |
|---|---|
| Branch de build | `++Augusta+release-0.6.0.3` |
| FileVersion / ProductVersion | `Augusta-CL-3207493` |
| Engine | UE 5.7 (strings `DEPRECATED (5.7)` no binário; dump `ue_version: 507`) |
| Empresa | Obsidian Entertainment, Inc. |
