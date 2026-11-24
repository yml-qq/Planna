# 168｜NOTE-05c-server-content-check-a：服务端上传内容签名校验

状态：代码已实现；本地 build/format/契约 **37 项**/OpenAPI 校验通过；CI 见 `VALIDATION.md`。

## 范围与验收

- 上传读取完整内容（≤5 MiB）后校验签名：
  - PNG `89 50 4E 47 0D 0A 1A 0A`、JPEG `FF D8 FF`、PDF `%PDF-`；
  - text/plain、text/markdown 必须为合法 UTF-8；
  - 其他声明类型拒绝。
- 不匹配返回 **415 `ATTACHMENT_CONTENT_MISMATCH`**；实际字节长度用于大小复核与入库。
- 冒烟新增：`image/png` + `"hello"` → 415。
- OpenAPI 上传描述补充服务端内容签名校验与错误码。
- 契约测试保持 **37 项**。

## 语义与边界

- 仅校验魔数/UTF-8，不证明文件完整或安全；不替代病毒扫描。
- 类型仍由客户端声明；扩展名/存储键由声明类型决定，未做扩展名与内容一致性重写。
- 文本按严格 UTF-8 校验（BOM 允许，非法序列拒绝）；二进制文本（非 UTF-8）会被拒绝。
- 上传现在先把 ≤5 MiB 读入内存再落盘，内存峰值随并发线性增加；仍无流式。
- 未校验 multipart 声明长度与实际长度一致性（以实际为准）。
- 无 AV/沙箱；PDF 预览仍依赖客户端 WebView。

## 后续

- `NOTE-05c-server-content-check-b`：病毒扫描、流式落盘、扩展名规范化与 PDF 外开降级。
