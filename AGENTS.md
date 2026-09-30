# AGENTS.md — エージェント用コーディング手順書（Pico Mic Drainer）

本ファイルは、このリポジトリで作業する AI エージェント向けの操作手順・ルールです。
特に **Windows PowerShell でのコマンド実行** に関する注意事項を定めています。

---

## 1. PowerShell コマンド実行ルール（最重要）

このリポジトリのビルド・git 操作は Windows PowerShell で実行される。以下の違反はツール呼び出しをエラーやハングさせてしまうため厳禁である。

### 1.1 `&&` と接合オペレーター `&` を禁止
- PowerShell は **`&&` を有効なステートメント区切りとして扱わない**。使用すると構文エラーとなる。
  - NG: `git add . && git commit -m "x"`
  - OK: `git add . ; git commit -m "x"`（**`;`（分号）で区切る**）
- 接合オペレーター `&` も、コマンドの連動実行には使わない。`&` は「引数リストを任意の命令に展開して呼び出す」オペレーターであり、連動実行の文脈では誤用されやすい。

### 1.2 ページャー待ち（q を打たないと終了しない）のコマンド禁止
以下のコマンドは対話的にブロックし、エージェントのツール呼び出しが**終了まで q キーを待つこと**になるため使用しない。

| 禁止パターン | 代替案 |
|---|---|
| `less` / `more` / `page` | 出力をそのまま端末に表示させる（pager を通さない） |
| `git log --oneline -15 \| less` などの git+pager | `git --no-pager log -n 15 --oneline` |
| 任意の `cmd \| cat` | PowerShell では `cat`=Get-Content が引数待ちしてブロックする。**`cat` のパイプは全面禁止** |
| インタラクティブにプロンプトが出るコマンド（`read`, `pause`, 未確認の `git config` 等） | 非対話的な形で実行するか、事前設定を前提とする |

出力の絞り込みが必要な場合は PowerShell 原生の cmdlet を使う：

```powershell
# git の出力を最後 5 行だけ見る（pager 不使用）
git --no-pager log -n 20 --oneline | Select-Object -Last 5

# ファイル内の特定テキストを探す
Select-String -Path PicoMicDrainer/Spec.md -Pattern "v3\.5\.0"

# ビルド結果の末尾だけ見る
dotnet build PicoMicDrainer\PicoMicDrainer.csproj -c Debug | Select-Object -Last 5
```

### 1.3 終了状態の確認は exit code を基準に
- 端末出力が文字化け（mojibake）することがある。**表示文字列より exit code が信頼できる**。
- 重要コマンドの末尾に `; echo "EXIT=$?"` を付けたうえで、`True`=正常（0）、`False`=エラー（非0）として解釈する。

---

## 2. ビルド・テスト手順

| 操作 | コマンド（リポジトリ根から実行） |
|---|---|
| デバッグビルド | `dotnet build PicoMicDrainer\PicoMicDrainer.csproj -c Debug` |
| リリース publish（win-x64 単一 exe） | `dotnet publish PicoMicDrainer\PicoMicDrainer.csproj -c Release -o bin\Release\net10.0-windows\publish\win-x64` |
| 変更内容確認 | `git status --short` / `git diff --stat`（`--no-pager` を併用） |

- ビルドは **エラー 0・警告 0** を確認してからコミットする。
- PowerShell で `2>&1` を使うと stderr が stdout に合流し、`$?` の意味が複雑化することがあるため、失敗時の詳細が必要な場合は別コマンドで再実行して確認する。

---

## 3. git 操作手順（このリポジトリの慣習）

- 作業ブランチ: `develop`、remote: `origin` → https://github.com/nezumi-tech/PicoMicDrainer.git
- コミットメッセージは以前の実装に倣う：
  - バージョン更新 → `Bump version to X.Y.Z`
  - パフォーマンス改善 → `perf: <説明> (issue N)`
  - バグ修正 → `<バグ名>修正：<説明>`（日本語可）
- コミットと push は、ユーザーが明確に依頼するまで**別々に**実施する（「コミットして push してください」は両方の依頼とみなす）。
- git grep / git show / git log に pager が介入しないよう **`--no-pager` を付与**する。

---

## 4. ファイル編集の慣習

- 全ファイル書き換え（rewrite）を行う場合は、生成した C# コメント・日本語テキストの誤字（ mojibake や混在文字 ）を **必ず推敲してから保存**。
- `.csproj` の `<Version>` は `<!--（アップデートするたびにここを書き換える）-->` コメント直下にあり、バージョン更新時はここを変える。
- `Spec.md` のバージョン参照（ヘッダー「適用バージョン」「状態」、§2.3 ビルド表、§7 変更履歴の (現行) 行、フッター）は `.csproj` と同期して更新する。
