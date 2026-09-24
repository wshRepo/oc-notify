# 项目级约束（oc-notify）

## Git 推送规则

⚠️ **重要**：`git push` 默认**只允许推送到 gitcode 远程**（`origin`，`git@gitcode.com:almoost/oc-notify.git`）。

- 用户说「push / 推送」但**未明确提到 github** → 只执行 `git push origin <branch>`
- 仅当用户**明确说明**「push 到 github」「推送到 GitHub」等含 github 的指令时，才允许 `git push github <branch>`
- 禁止未经用户明确要求同时推送到两个远程

## 远程仓库

| 名称 | 地址 | 用途 |
|------|------|------|
| `origin` | `git@gitcode.com:almoost/oc-notify.git` | 默认推送目标（主仓库） |
| `github` | GitHub 公开仓库 | 仅用户明确要求时推送 |

## 其他

- 提交前须经用户审核确认（沿用全局规则：不主动 commit/push）
- 提交 message 使用中文
