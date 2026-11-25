# 第一周纵向切片计划：从架构到可玩

> **实施状态（2026-11-25）**：A 侧已落地——三维真实战斗仿真 `CombatRuntime` 完成（运动/弹道/扫掠命中/敌舰 AI/胜负），
> `GameBootstrap` 已切到真实现，`tools/CombatSimCli` 可在无 Unity 环境跑完一局。关键偏差两条：
> ① 散布上限由 8° 改为 1.0°（8° 在 180 km 处横向 25 km，实际整场打不中）；
> ② 提前量必须用相对速度解（用目标绝对速度会漏掉本舰 30 km/s，命中率掉到 1%）。
> 详见 `docs/architecture.md` 的「真实战斗仿真：三维」。B 侧（Unity 外壳）尚未开始。
>
> 来源：对提交 `f1bfd1a` 的审查意见，整理为两人并行开发第一周的执行依据。
> 核心判断：底座已经够用——战斗输入自包含、战斗会话能被表现层驱动、结算能整体回滚、内容引用可校验，契约测试也较完整。**接下来暂停继续抽象框架，直接做第一个可玩的纵向切片。**

## 目标

不是先把所有 `Fake*Service` 替换掉，而是尽快得到这一条真实流程：

```text
进入 Unity
→ 点击“开始任务”
→ 进入战斗
→ 玩家操舵、瞄准、开火
→ 炮弹飞行并命中敌舰
→ 敌舰被击毁
→ 战斗结算
→ 返回战略界面，军功和资源发生变化
```

这条流程一旦跑通，项目就从“架构设计”进入“游戏开发”。

## 开工前先做半天准备

### 1. 补 Unity 的 `.gitignore`

目前仓库只忽略了 LaTeX 和 .NET 构建产物，直接打开 Unity 会产生大量本地文件。至少加入：

```gitignore
game/Library/
game/Temp/
game/Obj/
game/Logs/
game/UserSettings/
game/MemoryCaptures/
game/.vs/
game/*.csproj
game/*.sln
game/*.user
game/*.pidb
game/*.booproj
game/*.svd
```

注意：

- 不要忽略仓库中 `tools/BuildCheck/` 里的 `.csproj`。
- Unity 生成的 `.meta` 文件必须提交。
- 两个人统一使用仓库指定的 Unity `2021.3.0f1`，暂时不要一个人升级 Unity。
- 第一次打开后，把 `packages-lock.json`、场景和 `.meta` 文件作为一个独立提交。

### 2. 先验证底座能编译、测试能跑

在有 .NET 环境的机器上执行：

```bash
dotnet build tools/BuildCheck/Bootstrap.csproj
dotnet run --project tools/SmokeTests/SmokeTests.csproj
```

当前测试按 14 个场景块组织，包含更多个 `Check` 断言；计数口径以场景块为准。

## 第一周只分成两条开发线

### 开发者 A：真实战斗仿真

只负责：

```text
game/Packages/com.starpawn.combat/
content/ships/
tools/SmokeTests/ 中的战斗测试
```

新建：

```text
Runtime/Domain/
    CombatRuntime.cs
    CombatWorld.cs
    CombatShip.cs
    Projectile.cs
    Obstacle.cs

Runtime/Domain/Systems/
    MovementSystem.cs
    WeaponSystem.cs
    ProjectileSystem.cs
    DamageSystem.cs
    ObjectiveSystem.cs
    SimpleEnemyController.cs
```

`FakeCombatRuntime` 保留，继续用于战略流程和契约测试。真实实现命名为 `CombatRuntime`，完成后在 Bootstrap 中切换。

第一版只实现这些能力：

1. **运动**
   - `HelmCommand` 设置油门和转向。
   - 根据 `MaxSpeedKps` 和 `TurnRateDps` 更新位置、航向。
   - 能量分配影响最大速度。
   - 使用二维平面坐标，不扩展成完整三维机动。
2. **瞄准和开火**
   - `AimCommand` 保存瞄准点。
   - `FireCommand` 检查装填状态。
   - 生成真实飞行的炮弹，不能先做成瞬间命中。
   - 炮弹按 `ShellVelocityKps` 前进。
3. **碰撞和伤害**
   - 第一版把舰船、炮弹和陨石都看作圆。
   - 炮弹先检测陨石，再检测舰船。
   - 先扣护盾，再扣舰体。
   - 暂时不做跳弹、核心区、复杂装甲和部件损坏。
4. **简单敌舰**
   - 面向玩家。
   - 进入射程后保持距离。
   - 装填完成后开火。
   - 血量低于一定值时撤退，时间不足可以先省略。
5. **结果输出**
   - 正确生成 `BattleSnapshot`。
   - 发出 `Fire`、`Hit`、`Destroyed`、`Victory/Defeat` 事件。
   - 返回逐舰 `ShipBattleOutcome`。
   - 同一随机种子产生相同结果。

**A 的第一周验收标准：无 Unity 的情况下，输入一组命令能够完成一场真实战斗，炮弹、伤害、死亡和胜负均由规则计算。**

这一周不要实现：

- 复杂阵型；
- 四类分队 AI；
- 立方律自动平衡；
- 完整装甲与跳弹；
- 舰载机；
- 动态战术决策；
- 真实物理碰撞。

### 开发者 B：Unity 可玩外壳

只负责：

```text
game/Packages/com.starpawn.presentation/
game/Assets/Game/
game/Assets/Game/Bootstrap/
```

建议建立：

```text
Assets/Game/
    Scenes/
        BattleSandbox.unity
        StrategicSandbox.unity

    Prefabs/
        ShipPlaceholder.prefab
        ProjectilePlaceholder.prefab
        AsteroidPlaceholder.prefab

Packages/com.starpawn.presentation/Runtime/
    Battle/
        BattleSceneController.cs
        BattleInputController.cs
        BattleViewRegistry.cs
        ShipView.cs
        ProjectileView.cs
        ObstacleView.cs
        BattleHud.cs

    Strategic/
        StrategicSandboxController.cs
        BattleResultPanel.cs
```

第一版全部使用 Unity Primitive：

- 舰船：Cube；
- 炮弹：Sphere；
- 陨石：放大的低多边形 Sphere；
- 星空：纯色背景或简单 Skybox；
- 尾焰和命中特效：Particle System。

不要等 Blender 资产。

具体职责：

1. **战斗循环**
   - `FixedUpdate()` 中调用 `CampaignFacade.StepBattle()`。
   - 第一期把 Unity 固定步长设成 `1/30 s`。
   - `Update()` 中读取快照，更新画面。
   - 后续再增加插值，第一版不必提前优化。
2. **实体映射**

   使用：

   ```text
   EntityId → ShipView
   EntityId → ProjectileView
   EntityId → ObstacleView
   ```

   快照中出现新实体时创建 View，消失或死亡时隐藏、播放效果。

3. **玩家输入**
   - `W/S`：油门；
   - `A/D`：转向；
   - 鼠标：将屏幕坐标投影到战斗平面，发送 `AimCommand`；
   - 鼠标左键：`FireCommand`；
   - `1/2/3`：航行、均衡、交战三种能量预设。
4. **HUD**

   第一版只显示：

   - 舰体；
   - 护盾；
   - 装填进度；
   - 当前能量模式；
   - 敌舰数量；
   - 战斗结果。

5. **战斗结束**
   - 发现 `TryGetBattleResult()` 成功；
   - 调用 `SettleBattle()`；
   - 显示资源和军功变化；
   - 返回战略测试界面。

B 可以先用 `FakeCombatRuntime` 搭场景、输入和 HUD。它不会产生真正的炮弹轨迹，前两天先验证舰船创建、快照刷新、命令发送和结算界面；A 的真实运行时合入后再接炮弹和命中特效。

## 两人的代码边界

| 内容 | A 修改 | B 修改 |
|---|---:|---:|
| `com.starpawn.combat/Domain` | 是 | 否 |
| `com.starpawn.presentation` | 否 | 是 |
| `Assets/Game` | 否 | 是 |
| `content/ships` | 是 | 只读 |
| `CampaignFacade` | 原则上否 | 原则上否 |
| `CombatContracts.cs` | 共同决定 | 共同决定 |
| `GameBootstrap.cs` | 不直接改 | 负责最后装配 |

`CombatContracts.cs` 现在应当视作暂时冻结。确实缺字段时，两人先讨论清楚，再用一个很小的独立提交修改，不能在两个功能分支上分别改。

建议分支：

```text
feat/real-combat-runtime
feat/unity-battle-slice
```

每天至少合并一次能够编译的阶段结果，不要各自开发一周后再第一次联调。

## 第一次联调的时间点

不要等 A 完整实现敌舰 AI 才联调。按下面顺序推进：

| 时间 | A 的产出 | B 的产出 | 联调目标 |
|---|---|---|---|
| 第 1 天 | 舰船运动 | 场景、舰船 View、相机 | 舰船能在 Unity 中移动转向 |
| 第 2 天 | 炮弹创建与飞行 | 输入、炮弹 View | 点击后能看到炮弹飞行 |
| 第 3 天 | 碰撞、护盾、舰体伤害 | HUD、命中特效 | 命中后 HUD 正确下降 |
| 第 4 天 | 敌舰脚本和胜负 | 结果页面、场景返回 | 能完整打完一局 |
| 第 5 天 | 确定性与边界测试 | 流程打磨 | 形成可演示的灰盒版本 |

第一周结束时，应该能录下一段 30–60 秒的视频：

> 玩家驾驶一艘方块舰船，在小行星之间转向、瞄准、发射炮弹，击毁敌舰，随后看到军功和资源结算。

即使画面非常粗糙，这也是项目最重要的第一个成果。

## 第二周再补战略闭环

第一周完成后，分工调整为：

### A：继续打磨战斗

- 陨石遮挡；
- 能量分配真正影响推进、护盾和武器；
- 简单散布与瞄准提前量；
- 跟随 / 攻击两种分队指令；
- 两种敌舰行为；
- 调整舰船参数。

### B：把假战略流程替换成最小真实流程

优先级如下：

1. 类型化的舰船配置加载，替换 `CampaignFacade.ResolveStats()` 中的硬编码属性。
2. 任务日志运行时。
3. 星图两个节点的真实 UI。
4. `quest.first_contact` 的目标推进。
5. 战斗前任务简报和战斗后剧情选择。
6. 文件存档。

关系网、完整经济生产链和三阵营完整剧情仍然可以使用假实现或暂缓。

## 现在不要做的内容

为了快速产生可玩成果，下面这些至少延后两周：

- LLM 剧情对话；
- 三阵营完整流程；
- 复杂关系网可视化；
- 完整经济和生产链；
- 精细 Blender 舰模；
- 完整三维航行；
- 大规模舰队；
- 高级敌舰 AI；
- 复杂装甲、跳弹、部位损坏；
- 大量剧情文本。

当前真正的瓶颈已经不是架构，而是：

1. 仓库里还没有 Unity 场景和表现代码；
2. 战斗模块仍是按数量直接判胜负的假实现；
3. 没有一次玩家可以亲手完成的真实交互。

所以最有效的下一步是：**两个人同时围绕同一场战斗开发，一个负责“规则真的在运行”，另一个负责“玩家真的能看见并操作”，五天后在战斗结果接口处汇合。**
