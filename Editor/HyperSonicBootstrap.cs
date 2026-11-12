#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace HyperSonic.Editor
{
    /// <summary>
    /// シーンへの配置を不要にする自動起動。
    /// このパッケージがプロジェクトに入っているだけで、Play 開始時に
    /// Receiver と Streamer が自動で生成される (Hierarchy には出ない)。
    /// 既に手動で配置されている場合はそちらを尊重し、二重起動はしない。
    ///
    /// なぜプロジェクト側に何かしら必要なのか: Unity には外部からタッチ入力を
    /// 注入する公開 API が無く、公式 Remote は Unity 本体に組み込みで実装されて
    /// いるため、サードパーティ製ツールは全て「プロジェクトに小さな部品を
    /// 1回入れる」形になる。その代わり、入れた後はシーン側でやることは何も無い。
    /// </summary>
    [InitializeOnLoad]
    internal static class HyperSonicBootstrap
    {
        static HyperSonicBootstrap()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (EditorApplication.isPlaying) EditorApplication.delayCall += EnsureSpawned;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EnsureSpawned();
        }

        private static void EnsureSpawned()
        {
            if (!EditorApplication.isPlaying) return;

            var receiver = Object.FindFirstObjectByType<HyperSonicReceiver>();
            if (receiver == null)
            {
                var go = new GameObject("[HyperSonic] Auto");
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<HyperSonicReceiver>();
                go.AddComponent<HyperSonicVideoStreamer>();
            }
            else if (receiver.GetComponent<HyperSonicVideoStreamer>() == null)
            {
                receiver.gameObject.AddComponent<HyperSonicVideoStreamer>();
            }
        }
    }
}
#endif
