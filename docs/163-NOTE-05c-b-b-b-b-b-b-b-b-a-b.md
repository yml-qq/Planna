# 163｜NOTE-05c-b-b-b-b-b-b-b-b-a-b：预览 Blob URL 生命周期清理

状态：代码已实现；本地前端 `pnpm test`（104 项）与 `pnpm build` 通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 新增 `revokeAttachmentPreview(id)`：revoke URL 并从 map 删除。
- 清理时机：
  - 同一附件再次预览前；
  - 删除附件成功后；
  - 删除笔记时（先清理该笔记已加载附件的预览）；
  - 组件卸载时清理全部并清空 map。
- 预览错误仍写入附件提示（按附件 id）。

## 语义与边界

- 只清理**已生成**的 Blob URL；未预览过的附件无操作。
- 未缓存磁盘/内存字节，预览仍需重新请求。
- 删除笔记时只遍历 `notes.attachments[noteId]`（未展开附件列表的笔记可能没有可清理 URL）。
- 无自动化 DOM 测试验证 `URL.revokeObjectURL` 调用次数；依赖代码审查/构建。
- 未处理浏览器前进/后退或窗口隐藏时的清理。

## 后续

- `NOTE-05c…-b-b`：预览缓存、PDF/视频预览与 DOM/设备验证。
