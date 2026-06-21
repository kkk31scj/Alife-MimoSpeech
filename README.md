# Alife-MimoSpeech

Alife 插件 —— 小米 MiMo-V2.5-TTS 语音引擎。

支持唱歌、方言、情绪演绎、音频标签控制。基于 [MiMo API](https://platform.xiaomimimo.com)，让桌宠说出有温度的话。

## 功能

- 🎤 **唱歌** — AI 调用 `<Sing lyrics="歌词"/>` 自动生成歌曲
- 🗣️ **方言** — 台湾腔、东北话、四川话、粤语
- 🎭 **情绪风格** — 温柔、高冷、活泼、磁性、甜美等，由 Style 配置统一控制
- 👤 **人设定制** — Personality 字段描述角色语感，MiMo 自动适配声线
- 🎵 **预置音色** — 冰糖（默认）/ 白桦 / Mia / Milo / Dean

## 安装

### 方式一：插件市场（推荐）

Alife 插件市场搜索「MiMo语音」一键安装。

### 方式二：手动安装

1. 下载 [Alife.Function.MimoSpeech-1.0.0.zip](https://github.com/kkk31scj/Alife-MimoSpeech/releases)
2. 解压到 `{Alife数据目录}/Storage/Plugins/Alife.Function.MimoSpeech/`
3. 重启 Alife 或刷新插件列表
4. 编辑角色 → 模块列表勾选「MiMo语音」

## 配置

| 字段 | 说明 | 默认值 |
|------|------|--------|
| API Key | [platform.xiaomimimo.com](https://platform.xiaomimimo.com) 获取 | — |
| Voice | 音色选择 | 冰糖 |
| Style | 语音风格标签，多个用空格分隔 | 空（默认普通话） |
| Personality | 角色人设描述，塑造语感 | 空（MiMo 默认语感） |

Style 可选值：`温柔 高冷 活泼 严肃 慵懒 俏皮 磁性 甜美 沙哑 台湾腔 东北话 四川话 粤语 开心 悲伤 兴奋 委屈 怅然 无奈`

## 依赖

- Alife.Function.FunctionCaller
- Alife.Function.Speech

启用 MiMo 后请关闭其他语音模型（EdgeTTS / VITS 等），避免冲突。

## 许可证

MIT
