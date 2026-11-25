# 项目文件回收站 v0.2.0（空间热力清理舱版）

Windows 11 桌面应用，使用 C# / WinForms、WebView2 与原生 HTML/CSS/JS 构建。回收站读取与窗口代码改编自 [Modern Recycle Bin](https://github.com/Dannyzzy/Modern-Recycle-Bin/tree/d8787472eac290a4e5b6c24cc3b97c8edadc10af)，上游 MIT 许可及 WebView2 许可随应用分发。

版本变更记录请参见 [CHANGELOG.md](CHANGELOG.md)。

## 安装与启动

双击构建生成的 `release/ProjectRecycleBin-v0.2.0-x64.msi` 安装（支持直接覆盖升级 `v0.1.x`）。安装范围为当前用户，默认目录为 `%LOCALAPPDATA%\ProjectRecycleBin`。安装后从开始菜单打开「项目文件回收站」，可在 Windows 的「已安装的应用」中卸载。安装包尚未签名，应用运行需要 WebView2 Runtime。

使用 WiX 5.0.2 重新打包：`pwsh -NoProfile -File .\app\package.ps1`。如 WiX 未加入 PATH，可用 `-Wix` 指定 `wix.exe` 的路径。

### 直接运行构建目录

运行 `app/dist-redesign/ScriptRecycleBin.exe`。需要 Windows 11、.NET Framework 4.x 和 Microsoft Edge WebView2 Runtime，请保留同目录 DLL 与 `ui` 文件夹。

重新构建：`pwsh -NoProfile -File .\app\build.ps1 -OutputDirectory .\app\dist-redesign`

## 核心特性

- **空间热力清理舱界面**：深色石墨 / 浅色铝白一键切换（同步切换 Windows 11 标题栏深浅色），可拖拽调节左右分栏宽度并持久化；首次启动提供 5 步交互式聚光灯引导（随时可通过右上角 `?` 或 `Ctrl+K` 重新打开）。
- **交互式空间占比条与一键圈选**：顶部按类别 / 项目动态可视化回收站空间占比，点击色块直接下钻筛选；提供「缓存/日志」「超大文件 >50MB」「30天前」「多余重复副本」一键智能勾选预设。
- **高级搜索与多维筛选**：支持通配符（`*.log`）、体积表达式（`>50mb`）、扩展名（`ext:py`）与盘符（`drive:d`）组合搜索与结果高亮；左侧支持智能视图、盘符、时间段、文件类别、项目目录（支持系统文件夹选择器与高频目录建议）与删除批次筛选。
- **多视图与高性能虚拟滚动**：支持标准列表、高密度紧凑表与图片/视频缩略图网格三视图；支持按删除批次、项目目录、文件类别、重复内容、平铺 5 种分组与 5 维升降序排序；采用 DOM 节点复用池应对数万级文件无卡顿。
- **富媒体检视台与防误删锁**：支持源码离线语法高亮、行号、软折行、预览内查找、大日志 Tail 尾部查看、JSON 格式化、CSV 表格、图片缩放/棋盘底与视频封面帧；同 SHA-256 重复副本直达对比；支持「保留」防误删加锁保护（批量删除弹窗默认自动跳过）。
- **安全处置与桌面级效率**：支持恢复到原位置（带同名冲突预警）、恢复到指定目录（保留两者 / 跳过 / 覆盖）、复制导出、内置安全统计确认弹窗的永久删除与清空回收站；支持右键菜单联动打开原目录 / 复制路径 / 查看 Windows 属性，以及 `Ctrl+K` 命令面板、`Shift` 连选、操作日志抽屉和 CSV/JSON 清单导出。

## 性能优化与验证

- 后端采用 `Directory.EnumerateFiles` 流式分片推送、字节数预筛 + 8KB 首尾快速指纹 + 多线程并行 SHA-256 去重与本地哈希缓存（`%LOCALAPPDATA%\ScriptRecycleAssistant\hash-cache.tsv`）、异步可取消预览/缩略图缓存及删除后内存增量更新。
- 标记、项目目录、主题、分栏宽度与引导状态保存在 `%LOCALAPPDATA%\ScriptRecycleAssistant\settings.json`。
- 运行 `node .\tests\grouping.test.js` 验证分组与检索逻辑；运行扫描与文件操作测试：

```powershell
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dist = (Resolve-Path .\app\dist-redesign).Path
& $compiler /nologo /codepage:65001 /platform:x64 "/out:$dist\ScanTest.exe" /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Resolve-Path .\tests\ScanTest.cs).Path
& "$dist\ScanTest.exe"
```
