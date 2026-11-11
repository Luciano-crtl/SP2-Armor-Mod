# Contexto do projeto: mod de blindagem para SimplePlanes 2

Nov 6, 2026 · @Luciano

## Objetivo

Criar um mod BepInEx para SimplePlanes 2 que simule blindagem ao estilo War Thunder: uma peça de blindagem (tipo fuselagem) que, ao ser atingida por um projétil de canhão, gera fragmentos que danificam as peças atrás dela.

Outras repos de mods com BePinex: https://gravysp2mods.com, https://github.com/hahaha8459812/simpleplanes2-minimap-plugin, https://github.com/hahaha8459812/simpleplanes2-localization-plugin, https://github.com/hahaha8459812/simpleplanes2-mouse-flight-plugin, https://github.com/hahaha8459812/simpleplanes2-rangefinder-plugin, https://github.com/ScreamingMaggot/simpleplanes2-flight-control, https://github.com/NoraNekowww/SimplePlanes2-FlightAnalyzer

Escopo da fase atual: só canhões, não metralhadoras. Só jogo local; multiplayer fica de fora por enquanto.

**Quem é o usuário:** Luciano, iniciante total em programação e em Unity. Usa Windows e o Visual Studio em português, e conhece bem o SimplePlanes 2 como jogador. O jogo está em `C:\Program Files (x86)\Steam\steamapps\common\SimplePlanes 2`.

**Como o Claude deve ajudar:**

- Explicar em passos pequenos, cada um com uma verificação.
- Pedir o texto exato dos erros ou as últimas linhas de `BepInEx/LogOutput.log`.
- Dizer claramente o que foi testado e o que é só rascunho.
- Responder no idioma da última mensagem, porque o usuário alterna entre inglês e português.

**Material que não vem junto:** o código decompilado do `Game.dll` (o zip `gamedll.zip`, com 2.030 arquivos `.cs`) só existia na conversa anterior. No novo projeto, reenvie o zip para consultar as classes do jogo. Ele serve só para estudo pessoal e não deve ser redistribuído.

## Decisões tomadas

O mod será um plugin BepInEx em C# que escuta o impacto dos projéteis de canhão e cria fragmentos por cima, sem alterar a física do projétil.

- **Carregador:** BepInEx 5.4.23.5 x64 (Mono), da página oficial de releases, extraído na pasta do jogo. O jogo roda em Unity 6000 com Mono.
- **Ferramentas:** Visual Studio Community, sem o Unity Editor. O plugin é compilado contra as DLLs do jogo.
- **Plataforma do projeto:** .NET Standard 2.1 (`netstandard2.1`), porque o `Game.dll` é construído para isso.
- **Marcar peças (tags no nome):** as propriedades ficam num bloco entre colchetes no fim do nome, preservando o rótulo do jogador, por exemplo `Placa frontal [ARM t=20 h=100] [MOD d=1]`. `ARM` (só fuselagens): `t` espessura em mm, `h` dureza em % (100 = referência; controla o ângulo crítico de ricochete), `b` fragilidade em % (ainda sem uso). `CAN` (canhões): `f` número de fragmentos, `k` dureza do projétil (entra no ângulo crítico de ricochete). A penetração em mm (`p`) foi removida: um `p=` antigo no nome é ignorado. `MOD` (motores, tanques, hélices, fuselagens): `d` dano mínimo por fragmento. O formato antigo `ARM_20` não é mais lido. Código em `PartTags.cs` (`TagCodec`). Nenhum mod existente cria tipos de peça novos; o nome é salvo na aeronave e viaja no multiplayer.
- **Onde atuar:** patch Harmony em `CannonProjectileScript.OnCollisionEnter`. Os projéteis de canhão são `Rigidbody` e colidem por física.
- **Modelo de blindagem:** o impacto registra o ponto, a direção e o ângulo, e um sistema de fragmentos lança raios aleatórios em um cone atrás da blindagem. O projétil não é parado nem atravessa.
- **Dano:** cada fragmento chama `PartScript.OnDamaged(null, dano, ponto, direção)` na primeira peça que o raio atinge, ignorando a própria peça de blindagem.
- **Saída de depuração:** linhas de log em `BepInEx/LogOutput.log`, não linhas desenhadas.
- **Hotkeys, se um dia houver:** evitar `[`, `]`, `P`, `G`, `L` e Alt, porque o jogo já os usa ou eles causam problemas.

## O que já sabemos e o que já funciona

Os fatos abaixo vêm da leitura do código decompilado do `Game.dll`. O código rascunhado a seguir foi a primeira versão: já compilou e roda no jogo, mas evoluiu (ver Estado atual e os arquivos em `Armor Mod/`).

### Fatos lidos no código do jogo

- `Game.dll` é .NET Standard 2.1. `PartScript` fica em `Assets.Scripts.Craft.Parts` e `CannonProjectileScript` em `Assets.Scripts.Craft.Parts.Modifiers.Weapons`.
- **Metralhadoras:** as balas ficam num pool (`BulletPoolManager`), movidas por um job Burst. O acerto é tratado na thread principal em `OnBulletHit`, e a bala é destruída no primeiro acerto. O `BulletData` é compartilhado por arma, então o dano não é por bala.
- **Canhões:** `CannonScript.Fire()` instancia um prefab com `Rigidbody`. A massa é `ProjectileVolume * 11342 * 0.01`.
- **Colisão do projétil:** em `OnCollisionEnter`, o impacto é `|normal · relativeVelocity|`, reduzido pela razão de massas se o outro corpo for mais leve. Detona se for pelo menos `_impactDetonationForce` (padrão 5). O dano direto é `0.3 * impacto * ImpactDamageScalar`.
- **Direção do projétil:** o jogo guarda a velocidade do frame anterior em `_previousFrameVelocity` (campo privado, gravado no `FixedUpdate`). Use-o em vez da velocidade atual.
- **Explosão:** o projétil explosivo chama `Explode()`, que chama `FlightSceneScript.CreateExplosion("GeneralExplosion", posição, escala, direçãoDaExplosão, playerId, direçãoDoImpacto, tipoDeImpacto)`. Ainda não verificamos como a explosão causa dano às peças.
- **Pass-through:** o projétil ignora a própria aeronave até uma distância do canhão (`passThroughDistance`) e só depois passa a colidir com ela.
- **Dano à peça:** `PartScript.OnDamaged(int? atacante, float dano, Vector3 posição, Vector3 direção)` soma em `PartDamage`. Os níveis de dano são limiares multiplicados por `MaxHealth`. Em aeronave remota ele retorna cedo (`Aircraft.RemoteAircraft`); contra a aeronave de outro jogador o jogo chama `Aircraft.NetworkAircraft.DamagePart(atacanteId, peça, dano, ponto, normal)`, que manda um RPC ao dono. O mod faz o mesmo em `FragmentSpawner.ApplyDamage` (atacante = `projétil.Owner.NetworkAircraft.PlayerId`, guardado em `FragmentSpawner.Attacker`) e `RemoteArmorDamagePatch` suprime `DamagePart` em peças `[ARM]`. **Corrigido, ainda não testado em multiplayer.**
- **`Part.Name`:** aparece nos logs do jogo, mas não foi confirmado que é o nome editável pelo jogador. O plugin de rangefinder usa o campo de nome da peça no painel de propriedades do construtor.

### Código rascunhado (não testado)

**Projeto:** um `.csproj` no estilo SDK com `<TargetFramework>netstandard2.1</TargetFramework>` e uma `<Reference>` com `<HintPath>` e `<Private>False</Private>` para `BepInEx.dll` e `0Harmony.dll` (pasta `BepInEx/core`), e para `Game.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll` e `UnityEngine.PhysicsModule.dll` (pasta `SimplePlanes 2_Data/Managed`).

**Plugin:** uma classe com `[BepInPlugin("com.yourname.armorfragments", "Armor Fragments", "0.1.0")]` que herda de `BaseUnityPlugin`. No `Awake` ela guarda `Logger` num campo estático `Log`, chama `new Harmony("com.yourname.armorfragments").PatchAll()` e escreve `Armor Fragments loaded!`.

**Patch de impacto:**

```csharp
[HarmonyPatch(typeof(CannonProjectileScript), "OnCollisionEnter")]
internal static class CannonHitPatch
{
    private static readonly HashSet<int> Handled = new HashSet<int>();

    // Harmony injeta o campo privado: três underscores + nome do campo.
    private static void Prefix(CannonProjectileScript __instance, Collision collision,
                               Vector3 ____previousFrameVelocity)
    {
        if (collision.contactCount == 0) return;
        ContactPoint contact = collision.GetContact(0);
        PartScript part = contact.otherCollider.GetComponentInParent<PartScript>();
        if (part == null) return;
        // Depois de confirmar o nome da peça, filtrar:
        // if (part.Part.Name == null || !part.Part.Name.StartsWith("ARM_")) return;

        Vector3 direction = ____previousFrameVelocity.normalized;
        float speed = ____previousFrameVelocity.magnitude;
        float angle = Vector3.Angle(direction, contact.normal);
        angle = Mathf.Min(angle, 180f - angle);   // 0 = de frente, 90 = raspando

        ArmorFragmentsPlugin.Log.LogInfo("Shell hit part '" + part.Part.Name + "' at " +
            speed.ToString("F0") + " m/s, angle " + angle.ToString("F0") + " deg");

        if (!Handled.Add(__instance.GetInstanceID())) return;   // uma vez por projétil
        FragmentSpawner.Spawn(part, contact.point, direction, false);
    }
}
```

**Fragmentos:**

```csharp
internal static class FragmentSpawner
{
    private const int FragmentCount = 20;        // raios por impacto
    private const float ConeHalfAngle = 35f;     // graus
    private const float MaxRange = 30f;          // metros
    private const float DamagePerFragment = 5f;  // valor inicial, a calibrar
    private static readonly RaycastHit[] Hits = new RaycastHit[32];

    public static void Spawn(PartScript armor, Vector3 point, Vector3 direction, bool applyDamage)
    {
        int mask = -1 & ~LayerMask.GetMask("WorldSpaceUI");
        int partsHit = 0;
        for (int i = 0; i < FragmentCount; i++)
        {
            Vector3 dir = RandomInCone(direction, ConeHalfAngle);
            int count = Physics.RaycastNonAlloc(point, dir, Hits, MaxRange, mask,
                                                QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; PartScript target = null; Vector3 targetPoint = point;
            for (int h = 0; h < count; h++)
            {
                PartScript p = Hits[h].collider.GetComponentInParent<PartScript>();
                if (p == null || p == armor) continue;
                if (Hits[h].distance < best)
                { best = Hits[h].distance; target = p; targetPoint = Hits[h].point; }
            }
            if (target == null) continue;
            partsHit++;
            if (applyDamage)
                target.OnDamaged(null, DamagePerFragment * (1f - best / MaxRange), targetPoint, dir);
        }
        ArmorFragmentsPlugin.Log.LogInfo("Fragments: " + partsHit + " of " + FragmentCount);
    }

    private static Vector3 RandomInCone(Vector3 axis, float halfAngle)
    {
        Vector3 side = Vector3.Cross(axis, Random.onUnitSphere).normalized;
        return (Quaternion.AngleAxis(Random.Range(0f, halfAngle), side) * axis).normalized;
    }
}
```

**Fluxo de teste:** compilar, fechar o jogo, copiar a DLL de `bin/Debug/netstandard2.1` para `BepInEx/plugins/ArmorFragments/`, abrir o jogo e ler o log.

## Descartado e por quê

O erro mais caro até agora foi o tipo de projeto no Visual Studio; o resto são escolhas de projeto.

| Ideia | Motivo |
| --- | --- |
| Projeto .NET Framework 4.7.2 | O `Game.dll` é .NET Standard 2.1. O Visual Studio descarta a referência em silêncio e dá CS0246 ("namespace Assets não encontrado") mesmo com `Game` listado. Foi um erro do guia, já corrigido |
| Perfura / não perfura para canhões | O projétil é um `Rigidbody`, e fazer um corpo físico atravessar a fuselagem exigiria brigar com a física. O modelo de fragmentos deixa o projétil em paz |
| Perfura / não perfura para metralhadoras | Seria possível (retornar `false` em `OnBulletHit`), mas a 1000 m/s a bala anda cerca de 20 m por passo, supondo passo de 0,02 s, e acertos atrás da blindagem no mesmo passo se perdem. Fica para depois |
| `Debug.DrawRay` para ver os raios | Só desenha dentro do Unity Editor, não no jogo. Usamos linhas de log |
| Linhas desenhadas no mundo (`LineRenderer`) | No jogo publicado o shader some por shader stripping, segundo o plugin FlightAnalyzer. Exigiria trabalho de renderização próprio |
| Unity Editor e AssetBundles | Não são necessários para um plugin. Bundles feitos em outra versão do Unity não carregam. Se um dia houver visual novo, o caminho é enviar malha OBJ e reconstruí-la no jogo, como faz o SP2 Pack |
| Usar DLLs pré-compiladas de repositórios desconhecidos | Não foi inspecionado. Compile você mesmo e baixe o BepInEx só da página oficial |
| Ligar `FlightGizmos.Visible` | Ativa o modo de depuração física do jogo e polui a tela de linhas |
| Peça de blindagem nova (tipo de peça novo) | Nenhum mod existente consegue; a marca pelo nome da peça resolve sem isso |

## Estado atual

Tudo abaixo foi testado no jogo pelo Luciano.

- **Funciona:** o plugin carrega; o patch em `CannonProjectileScript.OnCollisionEnter` registra o impacto, o ângulo (correto também em fuselagens curvas e esferas) e filtra pelo nome `ARM_`; o nome editável da peça (`Part.Name`) é o usado pelo filtro.
- **Fragmentos:** cone de 20 raios atrás da armadura, com dano (`PartScript.OnDamaged`) e queda linear até 30 m. O dano está alto demais e será balanceado depois.
- **Ricochete:** tiro raspando na armadura gera um único raio refletido; os raios dos fragmentos também ricocheteiam em qualquer objeto, com ângulo tolerado menor. Limite de ricochetes por raio configurável.
- **Visual:** raios animados (cabeça com rastro que some) e bolhas coloridas nos impactos (laranja = armadura, ciano = ricochete, vermelho = peça atingida, amarelo = nada/objeto que não é peça). O `LineRenderer` funciona no jogo (um dos shaders testados existe).
- **Painel próprio no jogo:** botão "Armor Fragments" no menu de voo abre uma janela feita com a biblioteca de UI da Jundroo (Juicy), com status do último impacto, sliders e toggles de todas as configurações, "Reset to defaults", "Clear rays" e as últimas linhas do log. Código em `Armor Mod/UI/`.
- **Configuração:** `BepInEx/config/com.yourname.armorfragments.cfg` (seções Fragments, Ricochet, Debug).
- **Decompilados usados (só estudo pessoal, não vão para o repositório):** `Game.dll`, `Jundroo.Packages.dll` (contém Juicy e `DevConsole`), `Jundroo.Common.dll` e o mod de referência GravyMenu. Peças-chave: `PanelDialogScript`, `UserInterface.CreateDialog<T>`, `Jundroo.Juicy.ResourceLoader.LoadXml/GetScriptType` (patchados para entregar nosso layout e script), `MenuPanelScript.OnWidgetInitialized` (botão no menu) e os controles `ToggleControl`/`SliderControl`/`TextControl`/`HeaderScript`.
- **Seção no designer:** o painel de propriedades de peça do designer ganhou a seção "Armor Fragments" (`UI/DesignerSection.cs`, patches em `UI/DesignerPatches.cs`): toggles Armadura/Canhão/Módulo de dano conforme os modificadores da peça selecionada, e sliders que reescrevem as tags do nome (com desfazer). Ainda sem teste no jogo. Efeitos do módulo de dano (fogo, vazamento, explosão, desligar motor) ficaram para depois.
- **Bloqueio por energia (fase 3):** a energia do projétil é `½·m·v²` (massa do `Rigidbody` do projétil dividida por 0,01, porque o jogo guarda toda massa em 1/100 do valor real (o designer divide por 0,01 para mostrar kg); velocidade do frame anterior, em m/s; no tiro simulado, massa/velocidade/calibre configuráveis). A armadura cobra `ArmorResistance × (t ÷ cos θ) × área do calibre` (J por mm por cm²; abrir um tampão de aço pede só ~40, mas projéteis reais gastam quase tudo em deformação e calor, então o padrão é 600, que dá penetração parecida com a real; calibre do `CannonData` do canhão ou, se ele sumiu, `escala do projétil × 1000`). Se a energia não chega a esse valor, o tiro é bloqueado e não há fragmentos; a sobra aparece no painel como "Left for fragments" (ainda não usada para dimensionar o leque). Depois que o jogo trata a colisão, um projétil real que o nosso modelo resolveu (bloqueado, penetrou, ou parou numa peça sem `[ARM]` e virou raio balístico) é parado e aposentado pelo caminho de fim de vida do próprio jogo (velocidade zero e `SelfDestructTimer = 0`), para a física não ficar quicando com ele; só o ricochete deixa o projétil físico seguir. (O projétil quicava porque o dano de impacto do canhão fica em zero nos testes, então o jogo nunca o detona.)
- **Tiro simulado e raio-X:** o botão "Simulate shot" do painel de voo dispara um projétil imaginário da câmera para a frente (`ShotSimulator.cs`), passa pela mesma lógica de armadura (`FragmentSpawner.Spawn(..., simulated: true)`) sem dar dano e soma o dano que cada peça levaria (`FragmentSpawner.DamageSink`). A visão raio-X (`XRayView.cs`) esconde as peças próximas (`Renderer.forceRenderingOff`, inclusive o mesh combinado do `PartGroupScript`) e desenha cópias translúcidas dos meshes. Meshes legíveis ganham brilho de borda (rim light falso: `XRayRim.cs` recalcula as cores de vértice da cópia pelo ângulo com a câmera; vermelho forte nas partes mais danificadas); meshes ilegíveis caem para cor chapada. Partes mais danificadas são desenhadas depois (render queue maior). Custo: o brilho de borda é cálculo de CPU por vértice, então só recalcula quando a câmera se move, em fatias por frame (`XRayBudgetKiloVertices`), com limite de vértices (`XRayMaxRimKiloVertices`) e estatísticas na seção "X-ray performance" do painel (`XRayStats.cs`). Sem teste no jogo ainda.
- **Raio balístico (`BallisticRay.cs`):** atravessa peças sem `[ARM]` e para na primeira armadura (ou em algo que não é peça). O tiro simulado o usa a partir da câmera. Num tiro real que bate numa peça sem `[ARM]`, o jogo para o projétil e um raio balístico continua dali (alcance `BallisticRayRange`) até achar armadura, aplicando dano de verdade. Fragmentos: `[CAN f=]` por canhão, `DefaultFragmentCount` sem tag, `SimulatedFragmentCount` no tiro simulado.
- **Raio inicial e dano progressivo:** o raio inicial (o projétil; no tiro simulado, a câmera) é mais grosso (`InitialRayWidth`) e mais rápido (`InitialRaySpeed`) que os fragmentos, que só saem quando ele chega (`leadTime`). No raio-X as peças começam neutras e só ganham o visual de dano no instante em que o primeiro fragmento as toca (`DamageRecord.FirstTime`, calculado pela distância percorrida e `RaySpeed`). O tiro simulado nasce na posição exata da câmera e exige a câmera orbital (`OrbitCameraController`). O toggle "Lock shot to camera" guarda posição e direção da câmera orbital no instante em que é ligado (em coordenadas do mundo) e os tiros seguintes saem de lá, com a câmera livre.
- **Ricochete na armadura (fase 4):** `EnergyModel.RicochetCriticalAngle`: ângulo de referência (`ArmorRicochetAngle`, 70° com dureza igual e 800 m/s) menos 12° por dobro de dureza da armadura sobre a do projétil, mais 5° por dobro de velocidade, depois puxado para 90° pelo **overmatch** (razão calibre ÷ espessura de 1 a 3; a partir de 3 não ricocheteia), vezes `ArmorRicochetMultiplier`, mais um tremor aleatório de até ±10° × `RicochetRandomness`/100 (padrão 50%). Ricocheteia se o ângulo de impacto ≥ crítico. Energia de saída `E·sen²θ·(1−RicochetFriction)`; o resto fica na placa. Cada impacto em armadura loga `Ricochet check` com o detalhamento. Fragmentos ainda ricocheteiam pelo modelo antigo (`FragmentRicochetAngle`), até as fases 5 e 6.
- **Nuvem de fragmentos (fase 5, `FragmentCloud.cs`):** substitui o cone fixo de 35°. Orçamento: `(ShellFragmentShare + (1 − ShellFragmentShare) × fragilidade) × (E − E_armadura)` — o projétil se estilhaça por conta própria (`ShellFragmentShare`, padrão 0,2; no futuro dependerá do tipo de munição) mesmo com `b=0`, e a fragilidade da placa soma o resto. A massa total sobe, se preciso, para que nenhum fragmento voe mais rápido que o projétil (`2E/v²`), até a massa do projétil mais a do tampão. Direção de cada fragmento = gaussiana 2D em volta de um eixo: o eixo começa na trajetória e se inclina para a normal da placa (`AxisTilt × ângulo/90 × dureza relativa`); desvio base `SpreadBaseAngle` × (0,5 + fragilidade) × (1 + 0,3·espessura/100) ÷ (1 + 0,15·ln(1 + sobra/custo)), limitado a 2–45°; elipse mais larga no plano de incidência (`SpreadAnisotropy` × ângulo/90). Amostragem estratificada em espiral (ângulo áureo) com tremor pequeno fixo. Energia por fragmento ponderada pela densidade da gaussiana (centrais levam mais; modo simples: igual). Massa total (base) = fragilidade × massa do tampão de aço (`0,00785 kg/cm³ × área × espessura efetiva`) + 20% da massa do projétil; velocidade `√(2E/m)`; área `(m/ρ)^(2/3)` (modo simples: `SimpleFragmentAreaCm2`). O painel mostra orçamento e nuvem do último impacto; o log mostra `Fragment cloud`.
- **Pontos de parada persistentes:** num tiro simulado, o ponto onde cada fragmento termina (peça ou objeto) vira uma bolinha magenta (`RayKind.Landed`) que fica até o próximo teste (`RayDrawer.ClearPersistent`, chamado no início de `ShotSimulator.Fire` e no botão "Clear rays"). Na fase 6 esses pontos também marcarão onde o raio acabou a energia.
- **Propagação dos fragmentos (fase 6, `PartPhysics.cs`, `FragmentSpawner.TraceRay`, `BallisticRay.cs`):** cada fragmento é um tiro com energia, massa e área próprias. Ao tocar uma peça mede a **distância dentro dela pelo método A** (por collider: `Collider.Raycast` para a frente dá a entrada e para trás, a partir de um ponto além, dá a saída; os trechos dos vários colliders da peça são unidos) e paga energia: `k = ρ·A·x/m`; arrasto proporcional (padrão) `E_saída = E·e^(−k)`, ou força constante (`ConstantForceLoss`) `E_saída = E·(1−k)`. `ρ` = massa da peça (`LoadedMass ÷ 0,01`) ÷ volume estimado dos colliders (caixa/esfera exatas, malha convexa = 60% da caixa, resto = 50% do bounds) × `DensityModifier`. **O dano de uma peça é a energia que o fragmento deixa nela** (kJ × `DamagePerKiloJoule`; `DamagePerFragment` foi removido); armadura `[ARM]` não leva dano; `[MOD d=]` ignora dano abaixo de `d`. O fragmento atravessa até `MaxPartsPerFragment` peças e para quando a energia cai abaixo de 0,5% da inicial (ponto de parada calculado dentro da peça; no tiro simulado vira bolinha magenta persistente) ou no chão. Ricochete de fragmento: limiar fixo `FragmentRicochetAngle`, mantendo `E·sen²θ·(1−atrito)`; o resto vira dano na peça. **Raio balístico com perda:** o tiro simulado e o tiro real que parou numa peça sem `[ARM]` atravessam as peças comuns pagando energia pela mesma lei (a primeira peça do tiro real inclusive, sem somar dano nosso); chegam à armadura mais lentos (`v = √(2E/m)`), ou param no meio. No simulado o dano estimado dessas peças aparece no raio-X conforme o raio inicial passa.
- **Fase 7 (visual e dano do jogo na armadura):** a velocidade desenhada de cada raio é a real (`√(2E/m)`) × `VisualSpeedMultiplier` (padrão 0,05; substitui `RaySpeed` e `InitialRaySpeed`, removidos) e cada trecho do caminho tem a sua (`RayPath.SpeedTo`), então o raio desacelera ao perder energia; o rastro é um intervalo de tempo atrás da cabeça. O instante em que cada peça é tocada (raio-X) sai desses mesmos tempos (fragmentos: `FragmentSpawner.TraceRay`; raio inicial: `BallisticRay.DrawShellRay`, que preenche `Crossed.ArrivalTime`). O raio inicial marca com bolinha a entrada em cada peça comum atravessada; o fim (armadura, chão, ou onde acabou a energia) é bolinha laranja/magenta persistente no tiro simulado. Os fragmentos não ganham bolinha por peça atravessada (seriam centenas de objetos por tiro). `ArmorDamagePatch` suprime `PartScript.OnDamaged` em peças com `[ARM]` (`IgnoreGameDamageOnArmor`, padrão ligado): o jogo não destrói a armadura por impacto, explosão ou batida.
- **Estrutura do código:** `ArmorFragmentsPlugin` (configs e patches), `CannonHitPatch` (impacto), `FragmentSpawner` (raios, ricochete, dano), `RayDrawer`/`RayAnimation` (visual), `LastImpact`, `LogMirror`, `SafePatcher` (cada patch isolado) e `UI/`.
- **Pergunta em aberto:** a explosão do projétil explosivo deve acontecer dentro da blindagem, como no War Thunder, ou ficar só nos fragmentos? O usuário ainda não respondeu. Hoje a explosão do jogo continua como está.

## Plano: modelo de energia (decidido; fases 1 e 2 verificadas, fase 3 verificada, fase 4 verificada, fase 5 verificada, fase 6 verificada, fase 7 implementada sem teste ainda)

**Progresso:** fases 1 e 2 verificadas pelo Luciano. Diagnóstico: todos os colliders são convexos e legíveis (`MeshCollider convex=True` de 8 a 40 vértices, `BoxCollider`, `SphereCollider`), toda peça dá 1 entrada e 1 saída, peças com forma complexa usam 2 a 4 colliders; as distâncias dentro das peças ficam em 0,03–0,3 m, e o bloco de armadura em 0,2–0,6 m. `LoadedMass` está nas unidades escaladas do jogo e inclui a massa dos modificadores. O campo de nome não tem limite de caracteres. Fase 3 verificada (`EnergyModel.cs`): energia do projétil e absorção da armadura substituem a penetração (ver "Bloqueio por energia"). Fase 4 verificada: ricochete novo (ver "Ricochete na armadura"). Fase 5 verificada: distribuição direcional e orçamento dos fragmentos (ver "Nuvem de fragmentos"). Fase 6 verificada: propagação (ver "Propagação dos fragmentos"). Fase 7 escrita: velocidade visual por trecho, marcadores e supressão do dano do jogo na armadura (ver "Fase 7"). O plano de energia está completo; falta calibrar `DamagePerKiloJoule`, `ArmorResistance` e `DensityModifier`. Ideias futuras: tipos de munição (`ShellFragmentShare` por tipo), explosão interna, módulo de dano com efeitos.

Decidido em conversa com o Luciano. Substitui a penetração em mm e o cone fixo. Quando uma fase for implementada, mover o que ela descreve para "Estado atual".

### Modelo físico (ordem de cálculo)
1. **Energia do projétil:** `E = ½·m·v²`. Massa do jogo (`ProjectileVolume × 11342 × 0,01`), velocidade de `_previousFrameVelocity`, calibre do `CannonData.CaliberInMilimeters` do canhão que atirou (via `_cannonScript`). Conferir as unidades (massa escalada) antes de fixar valores absolutos.
2. **Armadura cobra energia:** `E_armadura = resistência × área do calibre × (espessura ÷ cos θ)` (θ medido da normal, `cos` com piso 0,1). A resistência é um valor global de referência (a dureza não entra aqui). `E < E_armadura` = bloqueado: toda a energia fica na placa. Não existe "ângulo de bloqueio".
3. **Ricochete:** ângulo crítico `θc = base(dureza da armadura)` ajustado pela velocidade e pelo **overmatch** (razão `calibre ÷ espessura`; acima de um limite fixo no código o ricochete é impossível; sem modificador global). A dureza do projétil (`[CAN k=]`) entra na dureza relativa. **Aleatoriedade (só no ricochete):** modificador global 0–100% (padrão 50%) que desloca o `θc` a cada impacto por um pequeno tremor (100% ≈ ±10°, 50% ≈ ±5°, 0% = determinístico). Energia de saída: `E × sen²θ × (1 − atrito)`; o resto fica na armadura (parte pode virar fragmentos, conforme a fragilidade).
4. **Orçamento dos fragmentos:** `E_frag = fragilidade × (E − E_armadura)`. Modo físico (padrão): cada fragmento tem massa própria (parte do projétil + massa de "spall" da placa, dada por fragilidade e espessura), energia ponderada pela direção (fragmentos centrais levam mais; pesos normalizados para a soma bater o orçamento), velocidade e área derivadas. Modo simples (opção): energia igual e área fixa configurável.
5. **Direção dos fragmentos (substitui o cone):** desvios angulares `(x, y)` sorteados de uma gaussiana 2D `N(μ, Σ)` num plano perpendicular a um eixo central. O eixo começa na trajetória e **se inclina para a normal da placa** quanto mais oblíqua a batida e mais dura a placa. `x` fica no plano de incidência e `y` é perpendicular; em batida oblíqua `σx > σy` (elipse). Largura σ: maior com mais fragilidade e mais espessura, menor com mais energia residual. Amostragem **estratificada** (faixas iguais de probabilidade) para cobertura estável com poucos fragmentos. A mesma máquina serve ao ricochete (distribuição estreita em volta da reflexão; prepara o "feixe de raios"). Parâmetros globais de teste: largura base, fator de inclinação para a normal, anisotropia. O painel mostra `σx`, `σy` e o desvio do eixo no último impacto.
6. **Cada fragmento é um tiro (sem sub-fragmentos por enquanto):** atravessa peças e perde energia em cada uma. Distância dentro da peça pelo **método A**: pares entrada/saída por collider (raycast para a frente e outro de trás para frente a partir de um ponto fora da peça), unindo os trechos para não contar duas vezes. Lei de perda: **arrasto proporcional por padrão** (`E_saída = E_entrada × e^(−2x/λ)`, `λ = 2m ÷ (ρ·A)`); opção **força constante** (`E_perdida = resistência × área × x`). Densidade das peças = massa ÷ volume estimado (malha legível, senão bounds do collider), com modificador global. Teto de peças atravessadas por fragmento.
7. **Dano de uma peça = energia dissipada nela × fator de calibração** (joules → vida do jogo), via `OnDamaged`. `[MOD d=]` continua como dano mínimo. O dano do jogo nas peças `[ARM]` é ignorado por enquanto: patch em `OnDamaged` que o suprime quando a chamada não é nossa (o Luciano também tira o dano do canhão ou aumenta a vida nos testes). O projétil real bloqueado continua por conta do jogo.
8. **Raio balístico:** no tiro simulado (e no real depois que o jogo para o projétil), perde energia em cada peça sem tag pela mesma lei dos fragmentos e chega mais fraco à armadura. No simulado aplica e mostra o dano estimado nessas peças; no real, a primeira peça já levou o dano direto do jogo, então só se desconta a energia (sem somar dano nosso).
9. **Velocidade visual vinculada à física:** a velocidade desenhada de cada raio é a velocidade real (calculada da energia: `v = √(2E/m)`) × um multiplicador global menor que 1, para dar para acompanhar. Ela diminui ao longo do caminho conforme o raio perde energia (velocidade por trecho), e o instante em que cada peça é tocada (raio-X) passa a sair dessas velocidades.
10. **Onde parou:** quando um projétil ou fragmento fica sem energia dentro de uma armadura ou peça, um marcador visual indica o ponto (novo tipo de marcador de parada, cor própria).

### Propriedades (tags no nome)
- `[ARM t= h= b=]`: `t` espessura (mm), `h` dureza (só ricochete), `b` fragilidade (quanto a placa estilhaça; o inverso de ductilidade).
- `[CAN f= k=]`: `f` fragmentos, `k` dureza do projétil. O antigo `p` (penetração) sai.
- `[MOD d=]`: dano mínimo (mantém).
- Globais (`.cfg` e painel de voo): dureza do projétil para canhões sem tag e **outra** para o tiro de teste; calibre, massa e velocidade do tiro simulado (substituem `SimulatedPenetration`); resistência de referência da armadura; atrito do ricochete; aleatoriedade; fator joules→dano; modificador de densidade; lei de perda (arrasto ou força constante); modo de fragmentos (físico ou simples); teto de peças por fragmento; parâmetros da distribuição direcional; multiplicador de velocidade visual.

### Fases (cada uma só vale depois da verificação)
1. **Diagnóstico do método A**, sem mudar comportamento: log de pares entrada/saída por peça no tiro simulado. *Verificação:* o Luciano manda o log numa fuselagem dele (peças sólidas ou cascas? colliders batem com a malha?).
2. **Propriedades e interface:** novas tags, controles no designer, configs e painel. Nada muda no cálculo. *Verificação:* as tags sobrevivem a salvar e carregar.
3. **Energia do projétil e absorção da armadura** (substitui a penetração); painel mostra energia, energia absorvida e resultado. *Verificação:* bloqueio e penetração coerentes com ângulo e espessura.
4. **Ricochete novo:** ângulo crítico, overmatch, tremor e energia de saída. *Verificação:* placa fina contra calibre grande nunca ricocheteia; tremor 0% repete o resultado.
5. **Distribuição direcional e orçamento dos fragmentos** (modos físico e simples). *Verificação:* a soma das energias bate com o orçamento; a nuvem de direções reage à obliquidade e à fragilidade.
6. **Propagação:** método A, densidade, as duas leis de perda, travessia de várias peças, dano por energia dissipada, raio balístico com perda de energia. *Verificação:* um fragmento atravessa duas peças e a segunda recebe menos dano.
7. **Dano do jogo na armadura suprimido; visual:** velocidade por trecho, marcadores nas peças atravessadas e nos pontos de parada, raio-X atualizado; atualizar este arquivo.

### Riscos e incertezas
Unidades de massa e velocidade; fidelidade dos colliders e peças ocas; volume das peças (aproximado); calibração do fator joules→dano e da resistência de referência (chutes iniciais); custo do método A (dois raycasts por peça por fragmento; o teto de peças e a medição de FPS são os controles); explosão de projéteis explosivos dentro da armadura continua adiada.

## Próximos passos

1. **Implementar o modelo de energia** (seção "Plano: modelo de energia"), fase por fase, começando pelo diagnóstico do método A. O balanceamento do dano (`DamagePerFragment`) passa a ser a calibração do fator joules→dano.
2. **Materiais de blindagem pelo nome da peça** (por exemplo `ARM_<material>_<espessura>`): cada material com seus multiplicadores de ricochete e de dano, sem criar peças novas. Depois, configurações integradas ao designer, também só pelo nome.
3. **Espessura:** ler o número do nome (`20` de `ARM_20`) e usá-lo para escalar fragmentos e dano com a energia do projétil (`0.5 * massa * velocidade²`) e o ângulo.
4. **Explosão interna:** decidir a pergunta em aberto. Antes, ler no `Game.dll` como a explosão causa dano às peças e suprimir/mover o `Explode()` original.
5. **Comandos no console nativo** (`DevConsoleApi.RegisterCommand`, no `Jundroo.Packages`), opcional.
6. **Mais tarde:** feixe de raios no ricochete em vez de raio único, metralhadoras, multiplayer, e conferir o mod depois de cada atualização do jogo.

## Visão raio-x do veículo (tecla)
- `VehicleXRay` (tecla `VehicleXRayKey`, padrão F8, via Input System; também o botão "Vehicle X-ray" no painel) mostra o veículo do jogador (`FlightSceneScript.Instance.LocalPlayer.Aircraft`) em raio-x, com todas as peças coloridas pelo dano REAL (`PartScript.PartDamage / MaxHealth`). Fica ligada até apertar de novo e atualiza as cores a cada 0,5 s (`XRayView.UpdateLive`).
- Escala de cores (tiro simulado e veículo): neutro azulado = sem dano, amarelo = pouco, laranja = danificado, vermelho = muito (`XRayView.DamageColor`). "Fully dark at" define a % de vida perdida que vale vermelho.
- Não testado no jogo. Custo: todas as peças do veículo ganham fantasmas; o limite de vértices do visual de borda (`XRayMaxRimKiloVertices`) protege o desempenho.

## Multiplayer: o host resolve os tiros de quem não tem o mod (fase 1, v0.3.0, não testado no jogo)
- **Como o jogo funciona:** o projétil do canhão só existe no jogo de quem atira (nos outros aparece só o clarão do cano, `SyncBool _showRemoteMuzzleFlash` → `CannonScript.PlayMuzzleFlash`). Quem atira detecta a colisão e manda ao servidor `RpcDamageRemotePartServer(atacante, idDaPeça, dano, posiçãoLocal, normalLocal)`; o servidor repassa ao dono (`RpcDamageRemotePartTarget` → `OnDamaged`).
- **Quem tem o mod calcula localmente** (alivia o host). O host só trata mensagens de dano cujo alvo é peça `[ARM]` e cujo atacante NÃO anunciou o mod (mensagem HELLO). Quem tem o mod já suprime a mensagem de dano do jogo em armadura (`RemoteArmorDamagePatch`).
- **`Net/HostResolver.cs`:** (1) `MuzzleFlashPatch` registra cada clarão de canhão remoto (pose, velocidade do avião + `ProjectileVelocity` no eixo do cano, arrasto lido do prefab do projétil); (2) `HostDamageMessagePatch` (Prefix em `RpcLogic___RpcDamageRemotePartServer___2803290691`, argumentos por posição `__0..__4`) segura a mensagem de dano em armadura; (3) a cada frame casa a mensagem com o disparo registrado que passa mais perto do ponto (voa o projétil com gravidade + arrasto, passo 0,02 s); (4) roda `FragmentSpawner.Spawn` com a massa/calibre/velocidade do canhão e `FragmentSpawner.Attacker`. Canhão sem `[CAN]` ou mensagem sem disparo que combine (metralhadora): a armadura não leva dano (o resto funciona como no jogo). Impacto em peça sem tag ainda NÃO é tratado (fase 2: raio balístico).
- **`Net/NetSync.cs`:** usa o canal de mensagens de peça do jogo (`INetworkAircraft.SendPartNetworkMessage`, tipos 200 = CONFIG e 201 = HELLO; `PartMessagePatch` intercepta `ProcessPartNetworkMessage`; o jogo ignora tipos desconhecidos). O host repete suas configurações globais a cada 5 s (e quando mudam); o cliente de servidor alheio aplica-as (`ApplyHostConfig`) guardando os valores do jogador e restaura ao sair (`RestoreUser`). `SaveConfig()` nunca grava os valores do host no `.cfg`. Quem não é host aceita CONFIG só de `OwnerId == 0`.
- **Configurações globais** (`NetSync.RegisterGlobal`, no plugin): tudo que decide o dano em outros jogadores (fragmentos, ricochete, energia, `IgnoreGameDamageOnArmor`...). Não-globais (visual, raio-x, logs, tiro simulado, tecla): continuam do jogador. No painel, cliente de servidor alheio não vê as seções Fragments/Ricochet nem os itens globais de Energy model.
- **Limitações conhecidas:** a pose do canhão remoto chega ao host com atraso (tolerância de pareamento 30 m + 0,4 × velocidade do atirador); sem o mod no host, nada disso funciona; `Aircraft.Velocity` de aeronave remota não foi verificado; `ReadString/WriteString` do FishNet não foi compilado.
- **Para testar:** host com o mod, outro jogador SEM o mod, canhão com `[CAN f= k=]` atirando em peça `[ARM]`: o log do host mostra `[HOST] Player N's shell hit ...`. Depois com os dois com o mod: o host não deve processar (nenhuma linha `[HOST]`).
