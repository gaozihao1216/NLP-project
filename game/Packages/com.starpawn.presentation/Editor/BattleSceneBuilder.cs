// Editor 菜单：一键生成可保存的战斗沙盒场景。
//
// 场景内容只有一个 BattleDemo 对象（挂 BattleDemoLauncher）——
// 其余一切（相机、星空、View）都在运行时程序化构建，场景文件因此始终极薄。
// 不手写场景 YAML，避免 .meta/GUID 配对问题。

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using StarPawn.Bootstrap;

namespace StarPawn.Presentation.Editor
{
    public static class BattleSceneBuilder
    {
        private const string ScenePath = "Assets/Game/Scenes/BattleSandbox.unity";

        [MenuItem("StarPawn/构建战斗沙盒场景")]
        public static void Build()
        {
            NewSceneSetup setup = NewSceneSetup.EmptyScene;
            var scene = EditorSceneManager.NewScene(setup, NewSceneMode.Single);

            GameObject go = new GameObject("BattleDemo");
            go.AddComponent<BattleDemoLauncher>();

            string dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[StarPawn] 战斗沙盒场景已保存：" + ScenePath +
                "。打开它按 Play 即可开战；不保存场景直接在任何空场景按 Play 也一样。");
        }
    }
}
