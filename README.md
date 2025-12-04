## 概要
このコードは、MusicBeeのプラグインです。

再生中の楽曲をMisskeyに投稿することができます。

## 機能
1. 再生中の楽曲をNowPlayingとして投稿することができます
2. 楽曲のアルバムアートを画像として添付することができます
3. ドライブ容量の節約のため、ファイルのIDを内部に保管して、同一曲の場合は再利用します
4. タイムラインを埋め尽くさないよう、任意の曲ごとに投稿することができます
5. カスタムなハッシュタグにも対応しています

## 使い方
1. `C:\Program Files (x86)\MusicBee\Plugins`に`./bin/Debug/`の3ファイルを入れてください
2. 必要に応じて、Pluginsの中に、「NowPlayingForMisskey」というディレクトリを作成してその中に入れても良いです
3. MusicBeeを再起動した後、編集→設定→プラグインで反映を確認してください
4. ディレクトリ内に3ファイルを入れている場合は、設定の右上で手動追加をします
5. その後、プラグインの「構成」でインスタンス名とトークンを入力します

## 動作保証
Misskey　2025.12.0

Windows11

MusicBee　3.6.9202

## 注意点
個人用のプラグインなので、諸々の潜在的不具合と脆弱性は許容しています。

1. MisskeyのTokenはローカル上に平文保管されます
2. Misskey側でファイルが消去された場合の挙動など、検証は不足しています

## 消去したい時
`C:\Users\{user_name}\AppData\Roaming\MusicBee\NowPlayingForMisskey`にTokenを含めた設定値・ファイルIDのログが保存されています。

これを削除すれば、Tokenの削除ができます。

## 作成者について
連絡先↓

https://misskey.seitendan.com/@takumin3211

## コードの出自について
1. MusicBeeのプラグインサンプル
MusicBee API

https://getmusicbee.com/forum/index.php?topic=1972.0
2. プラグイン作成のサンプル記事
MusicBeeプラグイン開発#1｜POEPOE

https://note.com/ads02360/n/nda389e295ef8
3. CodexCLI（GPT5.1-Codex）
