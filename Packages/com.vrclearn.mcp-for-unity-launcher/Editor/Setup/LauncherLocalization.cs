using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityLauncher.Editor
{
    public enum LauncherLanguage
    {
        English = 0,
        Japanese = 1,
        TraditionalChinese = 2,
        SimplifiedChinese = 3,
        Korean = 4
    }

    // Shared by both windows; this assembly must work without MCP for Unity.
    public static class LauncherLocalization
    {
        private const string PreferenceKey = "MCPForUnityLauncher.EditorLanguage";
        // Keep persisted enum values stable while placing Korean between Japanese and Chinese.
        private static readonly LauncherLanguage[] LanguageOrder =
        {
            LauncherLanguage.English, LauncherLanguage.Japanese, LauncherLanguage.Korean,
            LauncherLanguage.TraditionalChinese, LauncherLanguage.SimplifiedChinese
        };
        private static readonly string[] LanguageLabels = { "English", "日本語", "한국어", "繁體中文", "简体中文" };
        // English source keys also provide the fallback. Values follow the other four languages in selector order.
        private static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {
            { "Language", new[] { "言語", "언어", "語言", "语言" } },
            { "Automatically manage MCP for this project", new[] { "このプロジェクトの MCP を自動管理", "이 프로젝트의 MCP 자동 관리", "自動管理此專案的 MCP", "自动管理当前项目的 MCP" } },
            { "Opening a project starts the server and connects automatically. The shared server stays running while another managed Editor is open, and recovers after unexpected interruptions.", new[] { "プロジェクトを開くとサーバーを起動し、自動接続します。別の管理対象エディターが開いている間は共有サーバーが稼働し続け、予期しない中断から復旧します。", "프로젝트를 열면 서버를 시작하고 자동으로 연결합니다. 다른 관리 대상 에디터가 열려 있는 동안 공유 서버가 계속 실행되며, 예기치 않은 중단 후 자동으로 복구됩니다.", "開啟專案後會啟動伺服器並自動連線。只要其他受管理的編輯器仍開啟，共用伺服器就會持續運作，並在意外中斷後自動恢復。", "打开项目后会启动服务器并自动连接。只要其他受管理的编辑器仍在运行，共享服务器就会继续运行，并在意外中断后自动恢复。" } },
            { "HTTP Remote is selected. Launcher keeps your remote settings and does not start a local server.", new[] { "HTTP Remote が選択されています。Launcher はリモート設定を維持し、ローカルサーバーを起動しません。", "HTTP Remote가 선택되어 있습니다. Launcher는 원격 설정을 유지하고 로컬 서버를 시작하지 않습니다.", "目前使用 HTTP Remote。Launcher 會保留遠端設定，不啟動本機伺服器。", "当前使用 HTTP Remote。Launcher 会保留远程设置，不启动本地服务器。" } },
            { "MCP address: {0}", new[] { "MCP アドレス: {0}", "MCP 주소: {0}", "MCP 位址：{0}", "MCP 地址：{0}" } },
            { "Project connection", new[] { "プロジェクトの接続", "프로젝트 연결", "專案連線", "项目连接" } },
            { "Connected", new[] { "接続済み", "연결됨", "已連線", "已连接" } },
            { "Connecting and registering", new[] { "接続・登録中", "연결 및 등록 중", "正在連線及註冊", "正在连接并注册" } },
            { "Verifying project connection", new[] { "プロジェクト接続を確認中", "프로젝트 연결 확인 중", "正在驗證專案連線", "正在验证项目连接" } },
            { "Waiting for upstream reconnection", new[] { "MCP の再接続を待機中", "MCP 재연결 대기 중", "等待 MCP 重新連線", "等待 MCP 重新连接" } },
            { "Retry in {0} seconds", new[] { "{0} 秒後に再試行", "{0}초 후 재시도", "{0} 秒後重試", "{0} 秒后重试" } },
            { "Last project verification", new[] { "最終プロジェクト確認", "최근 프로젝트 확인", "上次專案驗證", "上次项目验证" } },
            { "Disconnected / waiting for automatic connection", new[] { "未接続 / 自動接続を待機中", "연결되지 않음 / 자동 연결 대기 중", "未連線 / 等待自動連線", "未连接 / 等待自动连接" } },
            { "Disconnected", new[] { "未接続", "연결되지 않음", "未連線", "未连接" } },
            { "Session ID", new[] { "セッション ID", "세션 ID", "工作階段 ID", "会话 ID" } },
            { "Shared supervisor", new[] { "共有監視プロセス", "공유 서버 감시 프로세스", "共用監控程序", "共享守护进程" } },
            { "Running", new[] { "稼働中", "실행 중", "執行中", "运行中" } },
            { "Not running / waiting for recovery", new[] { "停止中 / 復旧を待機中", "실행되지 않음 / 복구 대기 중", "尚未執行 / 等待恢復", "尚未运行 / 等待恢复" } },
            { "Last updated", new[] { "最終更新", "최근 업데이트", "最近更新", "最近更新" } },
            { "Registered Editors", new[] { "登録済みエディター", "등록된 에디터", "已註冊編輯器", "已注册编辑器" } },
            { "Services", new[] { "サービス", "서비스", "服務", "服务" } },
            { "Status", new[] { "状態", "상태", "狀態", "状态" } },
            { "Management", new[] { "管理方法", "관리 방식", "管理方式", "管理方式" } },
            { "Managed by Launcher", new[] { "Launcher が管理", "Launcher에서 관리", "由 Launcher 管理", "由 Launcher 管理" } },
            { "Using an existing server", new[] { "既存のサーバーを使用", "기존 서버 사용", "使用現有伺服器", "使用已有服务器" } },
            { "Recovery count", new[] { "復旧回数", "복구 횟수", "恢復次數", "恢复次数" } },
            { "Log directory", new[] { "ログフォルダー", "로그 폴더", "記錄資料夾", "日志目录" } },
            { "Open log directory", new[] { "ログフォルダーを開く", "로그 폴더 열기", "開啟記錄資料夾", "打开日志目录" } },
            { "Starting", new[] { "起動中", "시작 중", "正在啟動", "正在启动" } },
            { "Existing server", new[] { "既存のサーバー", "기존 서버", "現有伺服器", "已有服务器" } },
            { "Waiting for automatic recovery", new[] { "自動復旧を待機中", "자동 복구 대기 중", "等待自動恢復", "等待自动恢复" } },
            { "Unhealthy / checking", new[] { "異常 / 確認中", "서비스 이상 / 확인 중", "服務異常 / 正在檢查", "服务异常 / 正在检查" } },
            { "Port occupied by another application", new[] { "別のアプリがポートを使用中", "다른 애플리케이션에서 포트 사용 중", "連接埠被其他應用程式占用", "端口被其他应用程序占用" } },
            { "Projects have conflicting server settings", new[] { "プロジェクト間でサーバー設定が不一致", "프로젝트 간 서버 설정 충돌", "專案的伺服器設定互相衝突", "项目的服务器设置存在冲突" } },
            { "Waiting to start", new[] { "起動を待機中", "시작 대기 중", "等待啟動", "等待启动" } },
            { "Waiting for the last Editor to close", new[] { "最後のエディターが閉じるのを待機中", "마지막 에디터 종료 대기 중", "等待最後一個編輯器關閉", "等待最后一个编辑器关闭" } },
            { "Error", new[] { "エラー", "오류", "錯誤", "错误" } },
            { "Stopped", new[] { "停止", "중지됨", "已停止", "已停止" } },
            { "MCP Launcher Setup", new[] { "MCP Launcher セットアップ", "MCP Launcher 설정", "MCP Launcher 設定", "MCP Launcher 设置" } },
            { "MCP for Unity is installed. Launcher will automatically start and connect to the local server.", new[] { "MCP for Unity はインストール済みです。Launcher がローカルサーバーを自動起動し、接続します。", "MCP for Unity가 설치되어 있습니다. Launcher가 로컬 서버를 자동으로 시작하고 연결합니다.", "MCP for Unity 已安裝。Launcher 會自動啟動並連線至本機伺服器。", "MCP for Unity 已安装。Launcher 会自动启动并连接本地服务器。" } },
            { "Open Launcher", new[] { "Launcher を開く", "Launcher 열기", "開啟 Launcher", "打开 Launcher" } },
            { "Launcher requires MCP for Unity 10.3.0 or a newer 10.x version. Install it in this project; Launcher enables automatically after installation and recompilation.", new[] { "Launcher には MCP for Unity 10.3.0 以降の 10.x バージョンが必要です。このプロジェクトにインストールしてください。インストールと再コンパイル後、自動的に有効になります。", "Launcher에는 MCP for Unity 10.3.0 이상의 10.x 버전이 필요합니다. 이 프로젝트에 설치하세요. 설치와 재컴파일이 끝나면 Launcher가 자동으로 활성화됩니다.", "Launcher 需要 MCP for Unity 10.3.0 或更新的 10.x 版本。請在此專案安裝；安裝完成並重新編譯後，Launcher 會自動啟用。", "Launcher 需要 MCP for Unity 10.3.0 或更新的 10.x 版本。请在当前项目安装；安装完成并重新编译后，Launcher 会自动启用。" } },
            { "Install through VPM", new[] { "VPM でインストール", "VPM으로 설치", "透過 VPM 安裝", "通过 VPM 安装" } },
            { "For VCC / ALCOMD: add the VPM repository below, then install MCP for Unity in this project's package manager.", new[] { "VCC / ALCOMD 向け: 以下の VPM リポジトリを追加し、このプロジェクトのパッケージ管理で MCP for Unity をインストールしてください。", "VCC / ALCOMD 사용 시 아래 VPM 저장소를 추가한 후, 이 프로젝트의 패키지 관리자에서 MCP for Unity를 설치하세요.", "適用於 VCC / ALCOMD：新增下方的 VPM 套件庫，再於此專案的套件管理頁面安裝 MCP for Unity。", "适用于 VCC / ALCOMD：添加下面的 VPM 仓库，再在当前项目的包管理页面安装 MCP for Unity。" } },
            { "Open VPM installation page", new[] { "VPM インストールページを開く", "VPM 설치 페이지 열기", "開啟 VPM 安裝頁面", "打开 VPM 安装页面" } },
            { "Copy VPM repository URL", new[] { "VPM リポジトリ URL をコピー", "VPM 저장소 URL 복사", "複製 VPM 套件庫網址", "复制 VPM 仓库地址" } },
            { "Install through UPM", new[] { "UPM でインストール", "UPM으로 설치", "透過 UPM 安裝", "通过 UPM 安装" } },
            { "For any Unity project, without VRChat or VCC: open Window > Package Manager, choose + > Add package from git URL, then paste the URL below and install.", new[] { "VRChat や VCC を使わない Unity プロジェクトでも利用できます。Window > Package Manager を開き、+ > Add package from git URL を選択して以下の URL を貼り付け、インストールしてください。", "VRChat이나 VCC 없이 모든 Unity 프로젝트에서 사용할 수 있습니다. Window > Package Manager를 열고 + > Add package from git URL을 선택한 다음, 아래 URL을 붙여 넣어 설치하세요.", "適用於任何 Unity 專案，無需 VRChat 或 VCC：開啟 Window > Package Manager，選擇 + > Add package from git URL，再貼上下方網址並安裝。", "适用于任何 Unity 项目，无需 VRChat 或 VCC：打开 Window > Package Manager，选择 + > Add package from git URL，再粘贴下面的地址并安装。" } },
            { "Copy MCP for Unity UPM URL", new[] { "MCP for Unity の UPM URL をコピー", "MCP for Unity UPM URL 복사", "複製 MCP for Unity 的 UPM 網址", "复制 MCP for Unity 的 UPM 地址" } },
            { "Open Unity Package Manager", new[] { "Unity Package Manager を開く", "Unity Package Manager 열기", "開啟 Unity Package Manager", "打开 Unity Package Manager" } },
            { "Use one installation method per project. Automatic server startup also requires uv / uvx to be installed.", new[] { "プロジェクトごとにインストール方法を一つ選んでください。サーバーの自動起動には uv / uvx のインストールも必要です。", "프로젝트마다 한 가지 설치 방법만 사용하세요. 서버 자동 시작에는 uv / uvx 설치도 필요합니다.", "同一專案請只使用一種安裝方式。伺服器自動啟動功能也需要已安裝的 uv / uvx。", "同一项目请只使用一种安装方式。服务器自动启动功能也需要已安装的 uv / uvx。" } }
        };

        public static event Action LanguageChanged;

        public static LauncherLanguage CurrentLanguage
        {
            get
            {
                int saved = EditorPrefs.GetInt(PreferenceKey, (int)DetectLanguage(Application.systemLanguage));
                return Enum.IsDefined(typeof(LauncherLanguage), saved) ? (LauncherLanguage)saved : LauncherLanguage.English;
            }
        }

        public static void SetLanguage(LauncherLanguage language)
        {
            if (!Enum.IsDefined(typeof(LauncherLanguage), language)) language = LauncherLanguage.English;
            if (CurrentLanguage == language && EditorPrefs.HasKey(PreferenceKey)) return;
            EditorPrefs.SetInt(PreferenceKey, (int)language);
            LanguageChanged?.Invoke();
        }

        public static string Text(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            int language = Array.IndexOf(LanguageOrder, CurrentLanguage);
            return language > 0 && Texts.TryGetValue(source, out var values) ? values[language - 1] : source;
        }

        public static string Format(string source, params object[] arguments) => string.Format(Text(source), arguments);

        public static void DrawLanguageSelector()
        {
            var current = CurrentLanguage;
            int index = EditorGUILayout.Popup(Text("Language"), Array.IndexOf(LanguageOrder, current), LanguageLabels);
            var selected = LanguageOrder[index];
            if (selected != current) SetLanguage(selected);
        }

        private static LauncherLanguage DetectLanguage(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Japanese: return LauncherLanguage.Japanese;
                case SystemLanguage.Korean: return LauncherLanguage.Korean;
                case SystemLanguage.ChineseTraditional: return LauncherLanguage.TraditionalChinese;
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified: return LauncherLanguage.SimplifiedChinese;
                default: return LauncherLanguage.English;
            }
        }
    }
}
