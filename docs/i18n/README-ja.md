# MCP for Unity Launcher

[English](../../README.md) | **日本語** | [한국어](README-ko.md) | [繁體中文](README-zh-TW.md) | [简体中文](README-zh.md)

[MCP for Unity](https://github.com/VRCLearn/unity-mcp) 用の VPM アドオンです。Unity プロジェクトを開くとローカルサーバーを自動起動し、複数のエディターで共有しながら稼働を維持します。

- **自動起動**：プロジェクトを開くとサーバーを起動し、Unity のブリッジを接続します。
- **サーバーの共有**：A と B が同じサーバーを使用している場合、A を閉じても B は引き続きサーバーを利用できます。
- **障害からの復旧**：管理対象のサーバーが終了したり応答しなくなったりすると再起動します。監視プロセスが終了した場合も再起動します。

**この動作が必要なすべてのプロジェクトに Launcher をインストールしてください。** MCP for Unity のみをインストールしたプロジェクトでは、従来の起動・終了動作が維持されます。

## インストール

Unity 2021.3 以降と [uv](https://docs.astral.sh/uv/getting-started/installation/) が必要です。Launcher は MCP for Unity 10.3.x に依存します。初期の対象プラットフォームは Windows です。

1. uv が未インストールの場合は、公式の手順に従ってインストールしてください。インストール後に Unity を再起動し、エディターが `uv` と `uvx` を検出できるようにします。
2. [VPM インストールページ](https://vrclearn.github.io/MCP-For-Unity-Launcher/) を開き、**Add to VCC/ALCOMD** をクリックします。または、次のリポジトリ URL を手動で追加してください。

   ```text
   https://vrclearn.github.io/MCP-For-Unity-Launcher/index.json
   ```

   このリポジトリから **MCP for Unity** と **MCP for Unity Launcher** の両方をインストールできます。
3. 各プロジェクトのパッケージ管理画面で **MCP for Unity Launcher**（`com.vrclearn.mcp-for-unity-launcher`）をインストールします。VPM は依存する MCP for Unity も同じリポジトリからインストールします。
4. プロジェクトを開き、**Window → MCP for Unity** で **HTTP Local** を使用します。AI クライアントの HTTP 接続を一度設定してください。既定のエンドポイントは `http://127.0.0.1:8080/mcp` です。

以降は Launcher がローカルサーバーを起動し、エディターを自動接続します。初回は uv が Python とサーバーの依存パッケージをダウンロードするため、時間がかかる場合があります。AI クライアント側の MCP 接続設定は別途必要です。

## 複数のエディターで使用する

プロジェクト A と B の両方に 2 つのパッケージをインストールし、同じローカルサーバーアドレスを使用してください。両方のプロジェクトを開いた後で A を閉じても、B は共有サーバーを引き続き利用できます。最後の管理対象エディターを閉じると、Launcher は 10 秒の猶予期間後に、自身が起動したサーバーを停止します。

**Window → MCP for Unity Launcher** を開くと、サービスの状態、参加しているエディター、ログを確認できます。管理の切り替えは現在のプロジェクトにのみ適用されます。

## 対象範囲と検証状況

Launcher はローカル HTTP サービスを管理します。リモート HTTP の設定は従来の管理方式を維持します。既存の外部サーバーは再利用しますが、そのプロセスの管理権限は取得しません。

バージョン 0.1.0 は、Windows でのプロセステスト、Unity 2022.3.22f1 と MCP for Unity 10.3.0 を使用した C# コンパイル、実際のサーバーのライフサイクル検証に合格しています。実際に動作する 2 つの Unity エディターでの操作や、他の OS での動作は未検証です。詳細は[検証記録](../verification.md)を参照してください。

## ダウンロードとドキュメント

- [リリース](https://github.com/VRCLearn/MCP-For-Unity-Launcher/releases)：VPM ZIP、UnityPackage、パッケージマニフェストを提供しています。VPM の使用を推奨します。UnityPackage を使用する場合は、MCP for Unity を別途インストールしてください。同じプロジェクトでは 1 種類の配布形式のみを使用してください。
- [開発と公開](../development.md)
- [アーキテクチャ](../architecture.md)
- [問題の報告](https://github.com/VRCLearn/MCP-For-Unity-Launcher/issues)

## ライセンス

[MIT](../../LICENSE)。MCP for Unity は別の依存パッケージであり、その貢献者によって保守されています。
