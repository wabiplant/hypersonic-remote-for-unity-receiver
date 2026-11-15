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

        private static bool s_SpawnedThisSession;

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) EnsureSpawned();
            else if (state == PlayModeStateChange.ExitingPlayMode) s_SpawnedThisSession = false;
        }

        private static void EnsureSpawned()
        {
            if (!EditorApplication.isPlaying) return;
            if (s_SpawnedThisSession && HyperSonicReceiver.Instance != null) return;

            // HideAndDontSave の自動生成物は Play を跨いで残骸として残る。
            // FindFirstObjectByType では残骸を見つけられないため、従来は Play のたびに
            // 新規生成され、Receiver は自己破棄されても Streamer と GameObject が
            // 累積し続けていた (実測 100 個超)。また残った個体は次セッションで
            // 正常に動かないことがある。残骸は破棄してセッションごとに作り直す。
            // 手動配置された Receiver (HideAndDontSave でないもの) は従来どおり尊重する。
            // 残骸はシーンが無効 (IsValid=false) な浮遊オブジェクトとして残る。
            // アセット (プレハブ等) と区別するため AssetDatabase のパス有無で判定する。
            foreach (var old in Resources.FindObjectsOfTypeAll<HyperSonicReceiver>())
            {
                if (old == null) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(old))) continue;
                if (old.gameObject.hideFlags != HideFlags.HideAndDontSave)
                {
                    if (old.GetComponent<HyperSonicVideoStreamer>() == null)
                        old.gameObject.AddComponent<HyperSonicVideoStreamer>();
                    s_SpawnedThisSession = true;
                    return;
                }
                Object.Destroy(old.gameObject);
            }
            foreach (var old in Resources.FindObjectsOfTypeAll<HyperSonicVideoStreamer>())
            {
                if (old != null &&
                    string.IsNullOrEmpty(AssetDatabase.GetAssetPath(old)) &&
                    old.gameObject.hideFlags == HideFlags.HideAndDontSave)
                    Object.Destroy(old.gameObject);
            }

            var go = new GameObject("[HyperSonic] Auto");
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<HyperSonicReceiver>();
            go.AddComponent<HyperSonicVideoStreamer>();
            s_SpawnedThisSession = true;
        }
    }
}
#endif
