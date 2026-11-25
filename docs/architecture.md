# 框架骨架说明

这套骨架按 `development-plan.md` 的建议落地，目的是让 A、B 两人各守一边边界并行开工：A 负责战斗仿真与表现，B 负责战略规则、剧情与整合。**当前只有契约和假实现，没有真实业务逻辑**——先把边界固定下来，再往里填东西。

## 已建成的东西

```
game/
  Packages/                        12 个 Unity 包（模块边界）
    com.starpawn.foundation/         ID、时间、随机源、Result、错误码
    com.starpawn.{模块}/Runtime/
      Contracts/                    对外接口与数据结构（属于本模块）
      Domain/                       内部规则实现（当前为假实现）
    com.starpawn.application/       工作流与门面 CampaignFacade
    com.starpawn.infrastructure/    内容校验、存档
    com.starpawn.presentation/      表现层（待 Unity 侧填充）
  Assets/Game/Bootstrap/            唯一知道所有具体实现的地方
content/
  ships/ factions/ star-maps/       舰船数值、阵营策略、星图节点
  missions/ stories/                战斗关卡、剧情节点
  quests/                           任务日志条目 + 日志视图配置
docs/
  development-plan.md               GPT 提出的并行开发规划原文
  storyline.md                      剧情线与任务日志设计（单一事实来源）
```

一共 21 个 `.asmdef`。纯 C# 包（Foundation、各 Contracts/Domain、Application、Infrastructure）全部设 `noEngineReferences: true`，编译期就无法引用 UnityEngine——这是"规则与表现分离"的硬约束，不靠口头约定。

## 并行开发怎么开始

每个包目前都能独立演示：

| 工作包 | 现状 | 下一步 |
|---|---|---|
| A 战斗仿真 | `FakeCombatRuntime` 可按固定 spec 跑完整战斗 | 替换为真实运动/弹道/命中，保持 `ICombatRuntime` 不变 |
| B 战斗表现 | 无 Unity 代码，契约已定 | 读 `BattleSnapshot`，消费 `BattleEvent` |
| C 战略规则 | `Fake*Service` 各司其职 | 逐个替换真实现，接口不变 |
| D 关系政治 | `FakeRelationsService` 已能校验政治行为 | 填真实关系规则 |
| E 任务剧情 | `FakeNarrativeRuntime` 有两个节点与有后果的选择 | 扩产剧情内容，不改接口 |
| F 战略界面 | 待填 | 只调 `CampaignFacade`，不碰 Domain |
| G 星零副官 | `RuleBasedOfficer` 三种能力已分离 | LLM 对话异步接入，加超时降级 |
| H 基础设施与集成 | `GameBootstrap` 已装配出完整假闭环 | 补内容加载、存档、场景切换 |

**替换假实现的唯一位置是 `GameBootstrap.Create()`**。改那一个方法，其他代码不用动。

两人怎么切：A 只改 `com.starpawn.combat`，B 改其余包与 `com.starpawn.application`。
交界只有两处——`BattleStartSpec`（B 产出、A 消费）与 `CampaignFacade.SettleBattle`（A 的结果回传、B 执行）。
`BattleRules.ValidateSpec` / `ValidatePower` 是共用的校验入口，不要在两边各写一套。

## 三条不能破的规则

1. **业务模块之间不互相调用实现。** `Application` 引用各模块的 `Contracts`；`Domain` 只引用自己的 `Contracts` 和 `Foundation`。asmdef 已经在编译期卡死跨模块引用。
2. **战斗只接收准备好的输入。** `BattleStartSpec` 里没有军衔、剧情、经济对象。战斗结束时只返回 `BattleResult` 事实，奖励多少由 `SettleBattle` 工作流决定。改成长曲线不需要动战斗代码。
3. **剧情只提效果请求。** `StoryDecision` 返回 `StoryEffect[]`，由应用层分派给对应模块校验执行。剧情拿不到关系图的内部对象。

`SettleBattle` 落实四条结算约束：用 `BattleId` 防重复发奖；奖励任务 ID 取自 battle spec，不受门面当时状态影响；舰队损伤、资源、军功、关系、星系管辖、残骸缴获与剧情推进全在同一事务里；任一步失败全量回滚且可重试。

## 第一次联调

没有 Unity 也能验证的纯 C# 闭环：

```
GameBootstrap.Create()  →  CampaignFacade
  LaunchBattle()        →  战斗 spec
  （Step 直到出结果）
  SettleBattle()        →  舰队/资源/军功/关系依次更新
  ChooseStoryOption()   →  剧情效果分派执行
  ReadStory()           →  推进到新节点
```

把假战斗的 `TryGetResult` 换成真实现，就是第一轮整合。

## 已定决策（原四条待定问题）

四条已由团队拍板。每条写清决策、理由和落点。**注意：部分是设计决策，代码还没跟进**，见文末待办。

### 1. 战斗期间的战略时间：暂停

战斗开始时冻结战略层时间，视整场交火为瞬间发生。理由是现实战场上直接交锋时间占比很低，大部分时间处于对峙；把战略层冻结，玩家的注意力才能集中在操舵和瞄准上。

- **落点**：现有假实现已是暂停，无需改代码。写剧情时不要出现"激战三天三夜"这类台词，战斗在叙事上按一次短暂接舷战处理。
- **连带**：战斗结算产出的不只是战损，还包括**管辖领域扩张**（`Navigation`）和**战略资源掠夺**（`Economy`）。这两项是结算工作流的后果，不是战斗模块自己算的——`BattleResult` 仍只返回战损事实，`SettleBattle` 负责分派。

### 2. 坐标单位与仿真参数：星图和战斗分层

| 层 | 单位 | 类型 | 说明 |
|---|---|---|---|
| 星图（`Navigation`） | 光分 | `long` | 绝对坐标，总尺度 1000 光年 = 5.26 亿光分，`long` 装得下且预留银河系扩展 |
| 战斗（`Combat`） | 公里 | `float` | 局部坐标，以战场中心为原点，范围几十到几万公里 |
| 显示层 | 任意 | — | 比例尺随缩放动态换算，纯表现 |

换算常量 `1 光分 = 17,987,547 公里` 放 `Foundation`，只在进入战斗时换算一次。战斗物理不在银河系绝对坐标里算——否则浮点精度会被吃掉，数值表也会变成一坨科学计数法。

- 仿真步长仍为 `1/30` 秒。
- 场景内的陨石块按 `BattleStartSpec.RandomSeed` 程序化生成，优先用 `Foundation` 里的 `DeterministicRandom`，保证可复现；存档只存 seed，不存每块的状态。
- 内容版本号与存档版本号尚未单独定，先用 `0.1.0` 占位。

### 3. 舰船战斗参数：立方律骨架 + 小说锚点 + 定位微调

立方律决定量级：结构量与火力随舰体特征长度 L 三次方增长。但小说第 34 章的表不是立方关系（A 级巡逻艇到 G 级巡洋舰，乘员差 30 倍、主炮值只差 5 倍），说明大舰靠炮塔数量而非单炮口径堆火力。所以不能直接用立方律覆盖小说数值。

```
HP       = baseHP  × (L / L_ref)³ × roleFactor
主炮伤害  = baseGun × (L / L_ref)³ × 炮塔系数（取自小说表主炮值）
副炮伤害  = 主炮 × 0.3~0.6（按舰种定位）
能量池    = baseEnergy × (L / L_ref)³ × roleFactor
```

`roleFactor` 按定位取：主力舰 1.0、护航舰 0.8、侦察舰 0.6。L 按每级舰手填特征长度。

立方律的副作用是 4 倍长度 = 64 倍战力，大舰会碾压小舰（符合小说，但让数量优势失效）。补偿手段是小舰的机动与精度：转向更快、散布更小、能贴身打薄弱区。这两个参数独立于火力，不能省。

**新增战斗机制：能量三路分配。** 小说原文有明确设定——第 20 章"所有主能量用于补充防护罩"、第 22 章护罩 100% 能扛 C 级驱逐舰一发主炮而 20% 连巡逻艇齐射都扛不住、第 45 章"把防护罩的能源调给镭射炮增加威力"。据此：

- 推进 / 护罩 / 武器共用一个能量池，投入护罩的能量决定护罩强度
- 护罩平时关闭（耗能为由，原文第 22 章），激战开启
- 操作用三档预设（航行 / 均衡 / 交战），不做实时滑条——原作也是靠一句口令切换，且实时战斗中操舵、瞄准、分队指令已够占手

**陨石修正**：不是障碍物，而是**有血量的静止掩体**。炮弹先打陨石，打碎后暴露后方目标；完全静止，无速度无物理翻滚。尺寸按显示需求放大，不追求写实比例（现实中陨石多与舰体相当，但那样游戏里看不清）。

### 4. 阵营差异：配置驱动，能力矩阵暂不细化

三阵营共用一套规则，差异由配置承担。当前已有联邦与家族配置，银鹰需补最简配置。最初的"阵营能力矩阵"设想（火力/机动/单位三维）先记录不做——第一版只有一个阵营的完整剧情，矩阵没有验证场景，提前定值是空想。

资源托管策略：

| 阵营 | 成长资源 | 资源可见度 | 补给支付方 | 关系预设 | 剧情入口 |
|---|---|---|---|---|---|
| 万罗联邦 | 军功 | 仅作战信息 | 高层代管 | 高猜疑（陈宇 -70） | `tanglong` |
| 银鹰帝国 | 军功 | 仅作战信息（暂定） | 高层代管 | 表面忠诚、暗地派系（待细化） | 待定 |
| 家族势力 | 资源膨胀 | 全部 | 自付 | 家臣忠诚 | `sandbox` |

后期玩家升入高层后，资源面板从隐藏切到可见——切换点绑定**军衔 ID**，具体绑哪一级等成长系统定稿时再填，不在 UI 里另写逻辑。

叙事侧的落实放在 `docs/storyline.md` 和 `content/quests/`：任务日志按主线/支线/委托分组，支持单条追踪和星图导航引导。银鹰与家族的任务条目目前只是配置示例，用于验证共享架构，不含完整剧情。

## 编译与验证说明

已安装 .NET 8 SDK，`tools/BuildCheck/` 下按 21 个 asmdef 生成了对应的 csproj，**全部 21 个程序集实际编译通过**。`tools/SmokeTests/` 是关键契约测试（18 个场景块：重复结算、中途失败回滚、剧情重复选择、跨节点选项、战损继承、快照深拷贝、随机源上界、内容校验、内容驱动奖励、航行事务、战斗契约校验、剧情失败回滚、星系管辖与缴获、战斗结果深拷贝、四元数欧拉往返、扫掠碰撞、反喷回正、提前量与真实战斗推进）。

本机应用控制策略阻止新生成 DLL 的加载，测试**编译通过但未能执行**。换一台允许执行的机器：

```bash
cd tools/SmokeTests && dotnet run
```

Unity 侧：用 Unity 2021.3 打开 `game/` 目录。`StarPawn.Presentation` 目前无源文件，等表现层填充。

## 两人并行前的底座加固（2026-11-23）

先确认编译：21 个 BuildCheck 工程与 SmokeTests 工程全部实际编译通过。随后补了三处：

**1. 战斗契约自包含与共享校验。** `BattleStartSpec` 增加 `MissionId`、`OriginSystemId`、`TargetSystemId`，结算不再依赖门面当时的可变字段；`FleetEntry.SquadIndex` 限定 0..3。新增 `BattleRules.ValidateSpec` / `ValidatePower`，Application 与 Combat 共用同一份规则——四分队上限、负能量投入、重复舰 ID、非法战损都在进入战斗前拦下。`TryGetResult` 改为深拷贝，调用方改数组不会污染内部结果。

**2. 结算事务补全。** `Atomic` 去掉没用的操作 ID，把门面自己的结算标记、记账与当前 ID 也纳入检查点，并加重入保护（嵌套调用由最外层统一回滚）。剧情 `Evaluate` 改为返回 `CommandResult`，移进事务内——剧情推进失败会连带回滚资源、军功、关系与星系管辖，重试可以成功。

**3. 胜利后果落地。** 架构里承诺的领域扩张与战略缴获现在真的执行：胜利时在同一事务内调用 `Navigation.ClaimSystem`，并按击沉数发放合金。Navigation 的快照与检查点也改成深拷贝，外部改快照不影响内部状态。

本机应用控制策略仍阻止新生成 DLL 加载，测试编译通过但未能执行（0x800711C7）；换机运行 `cd tools/SmokeTests && dotnet run`。

## 审查修复记录（2026-11-22）

按代码审查意见完成六项修复，骨架从"静态检查通过"变成"可编译、可验证、失败后状态正确"：

**1. 编译阻断** — `HashSet<EntityId>` 替代 `battleId.Value`；两个 `SquadRole` 枚举保留在各自模块，应用层显式映射（`GameBootstrap` 的 asmdef 也补上了缺失的 Contracts 引用）。

**2. 战斗契约** — `FleetEntry` 带已解析属性与继承战损；`BattleResult` 返回逐舰战后事实；新增 `ObstacleSnapshot`（静止有血量掩体）和 `PowerAllocationCommand`（三档预设）；`IBattleSession` 让表现层通过门面推进战斗。（当时契约写的是"二维平面仿真 + 3D 表现"，已于 2026-11-25 整体改为三维，见下文。）

**3. 正确性** — `Foundation` 加 `IStateCheckpoint`，`Atomic()` 在动作前给六个服务打检查点，任一步失败全量回滚（含门面自己的已结算标记）。剧情改为 `Choose` 只校验、`CommitChoice` 才推进，杜绝重复发奖和跨节点选择。所有假实现的 `Read()` 改为深拷贝或重建，外部改快照不影响内部状态。

**4. 内容通路** — `BasicContentValidator` 真正读 JSON，校验 ID 唯一、剧情/任务/星图/舰船引用；补齐 `ship.d_destroyer`、`faction.empire`、`ch1.conflict` 三处悬空引用；奖励改由 `JsonMissionRewardSource` 从 `content/missions/` 读取，改 JSON 会改变结算数值。

**5. 确定性清理** — 关系键统一为 `from|to|kind`；经济拒绝负数与溢出；`PerformPoliticalAction` 真正执行；`SettleBattle` 纳入 `EvaluatePromotion`；`DeterministicRandom` 修零种子退化与浮点上界问题。

**6. 战损闭环** — `ShipReadiness`（Ready/Damaged/InRepair/Destroyed）决定出战资格；战斗继承长期损伤，逐舰回写；击沉舰退出舰队，下一场不会满血复活。

### 仍需注意

- **事务用回滚而非真正的候选状态隔离**：单机同步执行且无并发观察者，回滚等价于原子性；嵌套调用由最外层统一打检查点。将来若引入异步或多线程，必须换成真正的候选状态提交。
- **战斗契约有唯一校验入口**：`BattleRules.ValidateSpec` / `ValidatePower` 由 Application 与 Combat 共用。扩展分队数量或能量规则时只改这里，不要在两人各自的代码里另写判断。
- **空间维度**：仿真已是三维（2026-11-25）。`SpawnPoint` 带 z/pitch/roll，`HelmCommand` 给四通道，快照带位置/姿态/速度/角速度。二维时代的 `Heading` 字段已删除，表现层改用 `Yaw/Pitch/Roll`（与 Unity 的 `Quaternion.Euler(pitch, yaw, roll)` 同约定）。
- **内容校验器是最小实现**：覆盖当前 6 类配置，新增配置类型时需扩展 `SubDirs` 和引用检查。
- **任务日志运行时**：`content/quests/` 配置已就位，但运行时服务（读配置、算状态、出导航）尚未实现，见 `docs/storyline.md`。


## 真实战斗仿真：三维（2026-11-25）

`com.starpawn.combat/Domain` 里落了第一版真仿真 `CombatRuntime`，`FakeCombatRuntime` 保留给战略流程与契约测试。
`GameBootstrap.Create()` 已切到真实现，切回去只改那一行。

目录：

```text
Domain/
  Vec3(在 Contracts)  Quat.cs  CombatWorld.cs  CombatShip.cs  Projectile.cs  Obstacle.cs  CombatRuntime.cs
  Systems/
    MovementSystem.cs  WeaponSystem.cs  ProjectileSystem.cs  DamageSystem.cs
    ObjectiveSystem.cs  SimpleEnemyController.cs
```

### 1. 空间与命令契约

- 坐标按右手系理解：X 右、Y 上、Z 前；舰体局部 +Z 为舰首。单位：km、km/s、km/s²、度、度/秒。
- `SpawnPoint(x,y,z,yaw,pitch,roll)`，保留 3 参构造（等价于 z=pitch=roll=0），旧调用点不用改。
- `HelmCommand { Thrust, Yaw, Pitch, Roll, FlightAssist }`：`Thrust` 是主推力归一化输入（档位值，-0.5 反推），
  三轴是**角加速度输入**而不是转速；`FlightAssist` 是 sticky 开关，仿真记住最近一次值。
- 快照带位置、姿态（Yaw/Pitch/Roll）、速度三分量、三轴角速度。姿态内部用四元数，
  导出时才转欧拉角，约定与 Unity 的 `Quaternion.Euler(pitch, yaw, roll)` 一致。

### 2. 机动参数全部来自舰体配置

`content/ships/*.json` 的 `combat` 段新增 `hullRadiusKm`、`maneuver`（推力加速度 + 三轴转速上限 + 三轴角加速度）、
`maxSpreadDeg`。契约里对应 `ResolvedShipStats.HullRadiusKm` / `Maneuver` / `MaxSpreadDeg`（`ManeuverProfile` 七字段）。

代码里不再出现「0.6 倍、1.8 倍、4 倍、8 秒」这类倍率——它们只保留在 `ManeuverProfile.Derive()` 一处，
作为**配置缺失时的缺省**（供假实现与测试）。调手感改 JSON，不重编译。

三舰初值：巡逻艇 0.20 km / 4.0 km/s² / 45·27·81 °/s / 180·108·324 °/s²；驱逐舰 0.27 / 3.5 / 32·19·58 / 128·76·230；
巡洋舰 0.37 / 2.5 / 20·12·36 / 80·48·144。

`CampaignFacade.ResolveStats()` 里仍按「旗舰/非旗舰」写死，只是把同一组数值同步进去；
B 的配置加载器落地后整段删除。

### 3. 运动：无阻力 + 档位推力 + 反喷回正

- 推力沿舰首：`v += forward × MaxThrust × ThrustSetpoint × dt`，没有阻力项，松档位靠惯性滑行。
- 战术航速软上限 `MaxSpeedKps`（游戏性让步，不是物理）：超了等比缩回。
- 档位控的是**加速度**而不是航速，所以 HUD 必须同时显示档位与当前加速度，否则玩家以为档位没生效。
- 回正不是「把 ω 设成 0」，而是飞控给一个反向角加速度，和玩家输入共用同一条积分通道：

```text
u = |input|>0 ? input : (assist ? -ω/(α·dt) : 0)
u = clamp(u, -1, 1)
ω = clamp(ω + u·α·dt, ±MaxRate)
```

`|ω| ≤ α·dt` 时该式恰好把 ω 落到 0 且不越过，最后一 tick 的反喷量按剩余转速等比缩小（收力，不是阶跃）。
用巡逻艇的配置（45 °/s、180 °/s²）算：满转松键后 0.25 s 停住，期间余转 5.6°——这就是「松手还带一下」的手感来源。
死区 0.5 °/s；三轴独立判断，按住偏航时俯仰的残余转速仍要继续反喷。
`FlightAssist=false` 时 `u=0`，角速度永不衰减（纯牛顿），用来演示真空无阻尼。

滚转姿态回平是二级开关、**默认关闭**（太空没有黄道面这个「上」），带 `1-|forward·refUp|` 权重淡出，
避免俯冲到竖直时参考退化导致自己乱滚。

### 4. 武器、散布、碰撞

- 装填：`ReloadSeconds × (1.4 - 0.8 × WeaponsShare)`，交战档最快。
- 射界：固定炮有 ±15° 锥，瞄准点出锥时钳到锥边并上报 `OutOfArc` 事件。
- 散布：`(1 - Accuracy01) × MaxSpreadDeg`，绕理想方向做锥形偏移，每次开火固定消费两个随机数。
  上限取 **1.0°**：在 60% 射程上落点散布半径约 1.4 倍舰体半径，命中率落在三到五成。
  早先写的 8° 在 180 km 处横向 25 km，比舰体半径大两个量级，会让整场打不中——已作废。
- 炮弹世界速度 = 舰速 + 炮口方向 × 弹速（追击更快、迎头更慢）。
- 命中用**三维线段 vs 球、按两体相对位移**做扫掠：弹速 220 km/s × 1/30 s = 7.3 km/tick，
  舰体半径只有 0.2–0.37 km，点检测 100% 穿透；舰速 32 km/s 也有 1.07 km/tick，同样漏判。
  先判陨石再判舰船，取最近命中；护盾先扣，溢出进舰体。

### 5. 提前量必须用相对速度（一个会毁掉整场战斗的坑）

炮弹在世界系的速度含发射舰自己的速度，所以解提前量要用**相对速度**：

```text
rel    = targetPos - shooterPos
relVel = targetVel - shooterVel
解 |rel + relVel·t| = 弹速·t，取最小正根
瞄准点 = shooterPos + rel + relVel·t
```

早先版本用目标**绝对**速度（`targetPos + targetVel·t`），漏掉了自己这 30 km/s：
150 km 上偏差几度、几十公里，实测命中率约 1%，双方打到 300 s 仍分不出胜负。
修正后同一场景 69 s 结束，且「关掉提前量就打不死」——这条现在是可验证的对照实验，不是感觉。

### 6. 执行顺序与确定性

`Step()` 固定顺序：角速度输入归零 → 应用待处理命令 → 敌舰 AI → 运动 → 武器 → 弹道 → 伤害 → 胜负 → 时钟推进。
全场一个 `DeterministicRandom`，消费顺序固定（陨石生成 → 每次开火散布）；遍历按 `List` 插入序，
`Dictionary` 只查不遍历；四元数每 tick 归一化一次；炮弹 ID 用递增整数。

### 7. 验证方式

- `tools/CombatSimCli`：无 Unity 跑完一局，启动先打印每舰的 `maneuver` 与半径（调参唯一可见入口），
  再逐 0.5 秒打印位置/姿态/速度/血盾/装填，最后输出 `BattleResult`。参数 `--seed`、`--no-lead`、`--enemies`、`--seconds`、`--quiet`。
- 本机应用控制策略（0x800711C7，Defender 拦截新生成二进制）仍然阻止执行，CLI 与 SmokeTests **只能编译不能运行**。
  换机执行：`dotnet run --project tools/CombatSimCli`、`cd tools/SmokeTests && dotnet run`。
- 为确认数值可用，用等效的二维模型复算了同一场 2v3：6 个随机种子全部取胜，用时 69–129 s，无僵局；
  关闭提前量则 300 s 打不死任何一艘。
