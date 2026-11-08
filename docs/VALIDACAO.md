# Checklist de validação

Execute na ordem. Cada item indica onde conferir (log em `logs/overlay_*.log` ou o painel **Status** do menu).
Para mais detalhe, rode com `G2Overlay.exe --debug`.

## Compilação

- [ ] `dotnet build -c Release` termina com **0 erros** .
- [ ] `bin\x64\Release\net8.0-windows\win-x64\Config\` contém `config.json`, `offsets.json`, `signatures.json`.

## Inicialização (jogo fechado)

- [ ] Overlay abre sem erro; log mostra `G2Overlay … x64=True`.
- [ ] Log: `offsets.json já corresponde ao dump` **ou** `Dump importado … 36 aplicados, 0 alterados`.
- [ ] Log: `Aguardando o processo 'Grounded2Steam-Win64-Shipping.exe'…`.
- [ ] Com INSERT, o menu aparece no monitor principal; "Processo: aguardando…".

## Conexão (jogo aberto, num mundo)

- [ ] `Processo aberto: PID=… Base=0x…`.
- [ ] `AOB encontrado [GWorld] … → global RVA 0xA6C76E0` (e GNames `0xA499E40`, GObjects `0xA53BC20` nesta build).
- [ ] `FNamePool validado … Entrada 0 = "None"`.
- [ ] `GObjects validado … N objetos` (≈ 480 mil).
- [ ] `UStruct.SuperStruct validado em 0x40`.
- [ ] `GWorld validado … UWorld '<nome do mapa>'`.
- [ ] `Janela do jogo encontrada … classe='UnrealWindow'`.
- [ ] Menu → Status: estado `Running` / "Conectado".

## Layout em runtime

- [ ] `ULevel.Actors validado em 0xA0`.
- [ ] `USceneComponent.ComponentToWorld validado em 0x…` (esperado 0x1F0).
- [ ] `USceneComponent.Bounds validado em 0x…` (esperado 0x118).
- [ ] `Todos os offsets não refletidos foram validados` e `offsets.json gravado`.
- [ ] Menu → "Layout: Actors ok · C2W ok · Bounds ok".

## ESP

- [ ] Perto de criaturas (formigas, ácaros, pulgões…), aparecem caixas **verdes** envolvendo o corpo todo.
- [ ] As caixas acompanham o movimento sem atraso perceptível e não "flutuam" ao girar a câmera.
- [ ] Mudar o FOV nas opções do jogo mantém o alinhamento (FOV vem de `POV.FOV`).
- [ ] Janela com borda, sem borda e resoluções diferentes: o overlay fica exatamente sobre a área do jogo.
- [ ] Barra de vida acima da caixa: cheia e **verde** em criatura intacta.
- [ ] Ao atacar: a barra diminui proporcionalmente e muda de cor: amarelo (~50%) → laranja (~25%) → vermelho (≤10%).
- [ ] Com "Vida (números)" ligado, `atual/máx` bate com a barra de vida do próprio jogo.
  *Se o máximo divergir em NG+ ou modo personalizado, teste com `ApplyHealthMultipliers` = false.*
- [ ] Criatura morta some (ou fica cinza com "Mostrar mortos").
- [ ] O nome mostrado corresponde à espécie (RowName da DataTable, ex.: "Ant Soldier").
- [ ] Criaturas atrás da câmera não são desenhadas; nada é desenhado acima de "Distância máx.".
- [ ] O jogador local nunca aparece; outros jogadores só com "Mostrar jogadores".

## Coletáveis (Milk Molars e Tech Chips)

- [ ] Log: `Filtros de coletáveis: milkmolar, milknugget`.
- [ ] Perto de um Milk Molar: log `Classe coletável reconhecida: BP_Milk_Nugget_…` e `Novo coletável: 'Milk Molar…'`.
- [ ] Caixa **ciano** com o nome ("Milk Molar", "(grupo)" ou "(buggy)") e a distância.
- [ ] Depois de quebrar o molar, ele some (ou aparece "(coletado)" com "Mostrar já coletados").
- [ ] Menu → Status: `Milk Molars: N rastreados`.
- [ ] Se nenhum aparecer perto de um molar visível: rode com `--debug` e me mande o log.
- [ ] Log: `Filtros de coletáveis: milkmolar, milknugget, techchip`.
- [ ] Perto de um Tech Chip: `Classe coletável reconhecida (techchip): BP_…TechChip…` e `Novo coletável: 'Super Tech Chip (Ominent · Stolen)' … filtro='techchip' por classe row='TechChip_Ominent_Super_Stolen'`.
- [ ] Caixa **magenta** com o nome do chip; ao pegá-lo, ela some.
- [ ] Log: `palavras de item: Key, Recipe`. Perto de uma chave: caixa **amarela** "Key …"; perto de uma receita: caixa **laranja** "Recipe …".
- [ ] Pés/sementes de limão-taiti (KeyLime) e a bancada de receitas **não** recebem caixa.
- [ ] Perto de um baú enterrado: caixa **lilás** "Unique Chest (Ice Cart)" / "Unique Chest (Coaltana)"; depois de desenterrado, some (ou fica cinza com "Mostrar já coletados").
- [ ] Flechas no chão: caixa **verde-limão** "Arrow" com "Flechas no chão (Arrow)" ligado, mesmo com "Coletáveis e arenas de chefes" desligado; somem ao serem recolhidas.
- [ ] Arenas de chefes: caixa **vermelho-claro** "Boss: Tarantula" / "Boss: Centipede" / "Boss: Lizard" / "Boss: Subject V" no altar; durante a luta o rótulo mostra "(luta ativa)".
- [ ] Se um coletável não aparecer: pare ao lado dele, aperte **F9** (ou o botão "Diagnóstico" do menu) e envie o log — as linhas `[Diag]` mostram classe, nome do objeto e RowNames de tudo num raio de 15 m.

## AimBot

- [ ] Com "AimBot" desligado (padrão), nada muda ao segurar o botão direito.
- [ ] Ligado: aparece o círculo de FOV no centro da tela.
- [ ] Segurando a tecla com uma criatura dentro do círculo, o círculo fica avermelhado, a criatura ganha um contorno vermelho e um "+" no ponto de mira, e a câmera vai suavemente até ela.
- [ ] Soltando a tecla, a mira é liberada na hora.
- [ ] Outros jogadores nunca recebem o contorno vermelho, mesmo com "Mostrar jogadores" ligado.
- [ ] Criaturas mortas não são alvo.
- [ ] Log: `Pontos fracos de BP_Ant_…: LeftEyeWeakspot, RightEyeWeakspot` e `Pontos fracos de BP_Ladybug_Augusta_C: …Leg…`.
- [ ] Formigas: círculos amarelos nos olhos; joaninhas: nas pernas. Com "Mirar no ponto fraco", o "+" vermelho vai para o ponto fraco mais próximo da mira.
- [ ] Criatura sem ponto fraco: a mira volta para o corpo (altura do ponto de mira).
- [ ] Seu pet/buggy aparece no ESP com "[pet]" e nunca recebe o contorno vermelho (com "Ignorar pets e buggies" ligado, o padrão).
- [ ] Com o menu aberto ou com outra janela em foco, o AimBot não mexe o mouse.
- [ ] Se a câmera oscilar em volta do alvo: aumente "Suavização" (ou baixe `AimGain`). Se for lenta demais: o contrário.
- [ ] Se a câmera não se mover: log `SendInput falhou`. Rode o overlay com o mesmo nível de privilégio do jogo.

## Menu e entrada

- [ ] INSERT mostra/oculta o menu (com o foco no jogo).
- [ ] END encerra o overlay; log termina com `Overlay encerrado.`.
- [ ] Menu fechado: cliques passam para o jogo (atacar/construir funciona normalmente).
- [ ] Menu aberto: cliques no menu funcionam e não chegam ao jogo; o jogo não perde o foco.
- [ ] "Enable Overlay" desligado: o ESP some e o menu continua.
- [ ] "Show Menu" desligado fecha o menu.
- [ ] Barra de título arrasta o menu; posição persiste após **Salvar**.
- [ ] Sliders de distância e FPS têm efeito imediato.

## Robustez

- [ ] Alt-Tab para outro app: overlay some (com `HideWhenGameNotFocused`) e volta ao retornar.
- [ ] Minimizar o jogo: overlay some.
- [ ] Voltar ao menu principal do jogo e entrar de novo: log `UWorld mudou`, ESP volta a funcionar.
- [ ] Fechar o jogo com o overlay aberto: log `Processo do jogo … encerrado. Aguardando reconexão…`.
- [ ] Abrir o jogo de novo: reconecta sozinho; desta vez `Globais carregadas do cache`.
- [ ] Nenhuma exceção repetida no log durante 10 min de jogo.

## Desempenho (menu → Status, ou log `[Perf]`)

- [ ] FPS ≈ 60 (limite padrão).
- [ ] CPU do overlay < 1% (valor exibido é % do total de núcleos).
- [ ] RAM < 150 MB.
- [ ] `leituras/s` coerente (~60 × (2 + 3 × criaturas próximas)).
- [ ] Se a CPU passar de 1%: reduza o limite de FPS, a distância máxima ou aumente `EntityScanIntervalMs`.
