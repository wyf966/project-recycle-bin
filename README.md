# 项目文件回收站 v0.1（多文件预览版）

Windows 11 桌面应用，使用 C# / WinForms、WebView2 和 HTML/CSS。回收站读取与窗口代码改编自 [Modern Recycle Bin](https://github.com/Dannyzzy/Modern-Recycle-Bin/tree/d8787472eac290a4e5b6c24cc3b97c8edadc10af)。上游 MIT 许可及 WebView2 许可随应用分发。

## 安装与启动

下载 [v0.1 Windows x64 安装包](https://github.com/wyf966/project-recycle-bin/releases/download/v0.1/ProjectRecycleBin-v0.1-x64.msi) 后双击安装。安装范围为当前用户，默认目录为 `%LOCALAPPDATA%\ProjectRecycleBin`。安装后从开始菜单打开「项目文件回收站」，可在 Windows 的「已安装的应用」中卸载。安装包尚未签名，应用运行需要 WebView2 Runtime。

使用 WiX 5.0.2 重新打包：`pwsh -NoProfile -File .\app\package.ps1`。如 WiX 未加入 PATH，可用 `-Wix` 指定 `wix.exe` 的路径。

### 直接运行构建目录

运行 `app/dist-redesign/ScriptRecycleBin.exe`。需要 Windows 11、.NET Framework 4.x 和 Microsoft Edge WebView2 Runtime。请保留同目录 DLL 与 `ui` 文件夹。启动时会扫描当前 Windows 用户可见的回收站，首次扫描可能需要一些时间。

重新构建：`pwsh -NoProfile -File .\app\build.ps1 -OutputDirectory .\app\dist-redesign`

## 使用

- 搜索文件名或原路径，按项目、删除批次、时间和文件类别筛选。每行输入一个项目根目录并点击“应用目录”；优先匹配最长目录，未匹配则按原父目录归类。
- 顶部搜索、左侧筛选、中间浏览批次、右侧预览。底部显示已选文件数量，可清除选择或批量处理。
- 列表按滚动位置加载可见区域附近的行。“全选当前结果”包含符合筛选条件的所有条目，勾选项在滚动后仍保留。
- 根据文件名和扩展名归类脚本与源码、日志、缓存与临时文件、文档、数据与配置、图片、音视频、压缩包以及构建产物与模型。未知格式和无扩展名文件也会列出。
- 同目录相邻删除时间间隔不超过 30 分钟的文件作为一个批次。切换“重复内容”可按 SHA-256 查看内容相同的文件。哈希读取失败的文件保留在普通列表中。
- 点选行预览文本或常见图片，其他格式显示基本信息。文本最多显示 262144 个字符，支持 UTF-8 和带 BOM 的 UTF-16。HTML、SVG 和脚本均以纯文本显示。
- 恢复保留原路径；已有同名目标时跳过，不覆盖。复制导出时遇到同名文件会加序号。永久删除前会列出路径和数量；批量操作后显示每项结果。处理前会重新验证内容哈希。
- 整目录删除的文件按原目录结构展开，删除时间继承目录的删除时间。处理其中的单个文件后，空目录可能仍留在系统回收站中。
- 标记和项目目录保存在 `%LOCALAPPDATA%\ScriptRecycleAssistant\settings.json`。AI 工具记录尚未接入，来源统一显示“来源未知”。

## 验证状态与限制

已验证编译、分组规则和模拟回收站中的多文件递归扫描、分类及哈希。恢复、导出、永久删除的真实回收站端到端流程，以及同名冲突和部分失败场景尚未完成验收。扫描期间若文件不可读取或目录不可访问，界面会显示错误。重解析点会跳过。包含大量或大型文件时，初次扫描计算哈希可能使界面暂时无响应。

运行 `node .\tests\grouping.test.js` 验证分组。扫描测试可在应用构建后编译运行：

```powershell
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$dist = (Resolve-Path .\app\dist-redesign).Path
& $compiler /nologo /codepage:65001 /platform:x64 "/out:$dist\ScanTest.exe" /reference:System.Windows.Forms.dll (Resolve-Path .\tests\ScanTest.cs).Path
& "$dist\ScanTest.exe"
```
