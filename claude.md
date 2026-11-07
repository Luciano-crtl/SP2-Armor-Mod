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
- **Marcar peças (tags no nome):** as propriedades ficam num bloco entre colchetes no fim do nome, preservando o rótulo do jogador, por exemplo `Placa frontal [ARM t=20 h=100] [MOD d=1]`. `ARM` (só fuselagens): `t` espessura em mm, `h` dureza em % (100 = aço de referência). `CAN` (canhões): `p` penetração em mm. `MOD` (motores, tanques, hélices, fuselagens): `d` dano mínimo por fragmento. O formato antigo `ARM_20` não é mais lido. Código em `PartTags.cs` (`TagCodec`). Nenhum mod existente cria tipos de peça novos; o nome é salvo na aeronave e viaja no multiplayer.
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
- **Dano à peça:** `PartScript.OnDamaged(int? atacante, float dano, Vector3 posição, Vector3 direção)` soma em `PartDamage`. Os níveis de dano são limiares multiplicados por `MaxHealth`. Em aeronave remota ele retorna cedo, e o multiplayer usa `NetworkAircraft.DamagePart`.
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
- **Bloqueio de projétil:** se a penetração do canhão (`[CAN p=]`, ou `DefaultPenetration` sem tag) for menor que a espessura efetiva da armadura (`t × h/100 ÷ cos(ângulo)`), não há fragmentos. O projétil em si não é destruído (decisão em aberto).
- **Estrutura do código:** `ArmorFragmentsPlugin` (configs e patches), `CannonHitPatch` (impacto), `FragmentSpawner` (raios, ricochete, dano), `RayDrawer`/`RayAnimation` (visual), `LastImpact`, `LogMirror`, `SafePatcher` (cada patch isolado) e `UI/`.
- **Pergunta em aberto:** a explosão do projétil explosivo deve acontecer dentro da blindagem, como no War Thunder, ou ficar só nos fragmentos? O usuário ainda não respondeu. Hoje a explosão do jogo continua como está.

## Próximos passos

1. **Balancear o dano** (`DamagePerFragment`) contra o dano direto do projétil e contra `MaxHealth`, usando o painel.
2. **Materiais de blindagem pelo nome da peça** (por exemplo `ARM_<material>_<espessura>`): cada material com seus multiplicadores de ricochete e de dano, sem criar peças novas. Depois, configurações integradas ao designer, também só pelo nome.
3. **Espessura:** ler o número do nome (`20` de `ARM_20`) e usá-lo para escalar fragmentos e dano com a energia do projétil (`0.5 * massa * velocidade²`) e o ângulo.
4. **Explosão interna:** decidir a pergunta em aberto. Antes, ler no `Game.dll` como a explosão causa dano às peças e suprimir/mover o `Explode()` original.
5. **Comandos no console nativo** (`DevConsoleApi.RegisterCommand`, no `Jundroo.Packages`), opcional.
6. **Mais tarde:** feixe de raios no ricochete em vez de raio único, metralhadoras, multiplayer, e conferir o mod depois de cada atualização do jogo.
