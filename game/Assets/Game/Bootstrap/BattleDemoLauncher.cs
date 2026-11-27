// 战斗 Demo 的组合根（Bootstrap 组装件，唯一允许调用 GameBootstrap 的地方）。
//
// 零场景启动：任何场景按下 Play，[RuntimeInitializeOnLoadMethod] 都会走到这里，
// 自动装配出星空、相机、HUD 与一场真实的三维战斗。不需要手摆任何物体。
// 若要一个可保存的场景文件，用 Editor 菜单 StarPawn/构建战斗沙盒场景。

using System.IO;
using UnityEngine;
using StarPawn.Application;
using StarPawn.Presentation;

namespace StarPawn.Bootstrap
{
    public class BattleDemoLauncher : MonoBehaviour
    {
        private static bool _started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (_started) return;
            _started = true;

            GameObject go = new GameObject("BattleDemo");
            go.AddComponent<BattleDemoLauncher>();
        }

        private void Awake()
        {
            string contentRoot = FindContentRoot();
            if (contentRoot != null)
            {
                Debug.Log("[BattleDemo] 舰船配置目录: " + contentRoot);
            }
            else
            {
                Debug.LogWarning("[BattleDemo] 找不到 content/ 目录，使用内置镜像数值（调 JSON 不生效）");
            }

            // contentRoot 给了就必须全对：读不到/读错会抛 InvalidOperationException——
            // 这是既定决策（装配期不吞错误），别在这里 catch 成"悄悄用内置表"。
            CampaignFacade facade = GameBootstrap.Create(true, contentRoot);
            BattleSceneController.Launch(facade, () => GameBootstrap.Create(true, contentRoot));
        }

        /// <summary>
        /// 从 game/Assets 向上找仓库里的 content/（含 ships 子目录）。
        /// 找不到返回 null，走 GameBootstrap 的内置镜像分支——那是显式选择，不是兜底。
        /// </summary>
        private static string FindContentRoot()
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(UnityEngine.Application.dataPath); // <仓库>/game/Assets
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "content");
                    if (Directory.Exists(Path.Combine(candidate, "ships"))) return candidate;
                }
            }
            catch (IOException)
            {
                // 目录枚举失败按"没找到"处理。
            }
            return null;
        }
    }
}
