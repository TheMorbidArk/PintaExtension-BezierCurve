# 为什么插件是单个 DLL

这是一次真实 bug 的事后复盘，那个 bug 在 0.1.0 里发布过。留成设计笔记，免得它再回来。

## 缺陷

0.1.0 的 `.mpack` 里有三样东西：

```
addin.info
TheMorbidArk.PintaExtension.BezierCurve.dll
icons/hicolor/{16x16,22x22,24x24,32x32,48x48,scalable}/actions/tool-bezier-curve-symbolic.*
```

安装正常，工具出现在工具箱里，一切都没问题——直到卸载插件：

```
uninstall 后残留：6 个 png/svg，目录全部删不掉
  warning: Directory .../icons/hicolor/24x24 could not be deleted.
registry 仍残留：TheMorbidArk.PintaExtension.BezierCurve,0.1.0 (enabled=True)
```

下次启动时，Pinta 的注册表里还有这个扩展节点，尝试实例化它，但程序集已经不在了，于是
崩溃：

```
System.IO.FileNotFoundException: Could not load file or assembly 'TheMorbidArk.PintaExtension.BezierCurve'
   at Mono.Addins.AddinEngine.CurrentDomainAssemblyResolve(...)
   at Pinta.MainWindow.UpdateExtension(ExtensionNodeEventArgs args)
```

## 根因

`Mono.Addins.Setup` 删除的是记录在插件名下的文件。清单（`addin.info`）是从程序集生成的，
只提到程序集本身：

```xml
<Addin id="TheMorbidArk.PintaExtension.BezierCurve" version="0.1.1" category="Tools">
  <Header>…</Header>
  <Runtime>
    <Import assembly="TheMorbidArk.PintaExtension.BezierCurve.dll" />
  </Runtime>
</Addin>
```

图标是在打包之后追加进包的。安装时它们被解包，但从未被记录为属于这个插件，所以卸载器
删掉了 DLL，留下了图标，然后删不掉目录——这也让注册表项无法清理。

第二个无关的麻烦：Mono.Addins 不能删除运行中进程已加载的程序集，所以卸载总是把文件删除
*计划*到下次启动。只要没有别的东西卡住，这没问题。

## 修法

只发一个文件：

* 图标是 `<EmbeddedResource>` 条目（`res/hicolor/…/tool-bezier-curve-symbolic.*`），
* `CurveAddin.RegisterIcons()` 在启动时把它们解包到
  `~/.cache/pinta-extension/bezier-curve/<version>/icons`，
* 那个目录被追加到 GTK 图标主题搜索路径，
* `.mpack` 现在只包含 `addin.info` 和 `TheMorbidArk.PintaExtension.BezierCurve.dll`。

由 `scripts/package-test.sh` 端到端验证：通过 Pinta 自己的注册表安装，重启 Pinta（强制
插件从注册表来），通过注册表卸载，再重启：

```
install    → registry: TheMorbidArk.PintaExtension.BezierCurve,0.1.1 … path=…/addins/addins/TheMorbidArk.PintaExtension.BezierCurve.0.1.1
run        → 23 tools, curve drawn, all checks pass
uninstall  → registry entry gone immediately, 1 file pending removal
restart    → no leftover files, no stale registry entry, no FileNotFoundException
```

## 教训

* `.mpack` 带的任何东西都必须是 Mono.Addins 知道的；否则卸载会静默地变成只卸一半。
* 载荷变了就要改插件版本：同 id + 同版本会被当成“已安装”，不会被替换。
* 一个残留的图标目录就足以留下悬空的扩展节点，而悬空的扩展节点会让 Pinta 下次启动崩溃。
