# Visual Regression Matrix

## Snapshot 场景

| Surface | State | Theme | Density |
|---|---|---|---|
| Dashboard | ready | light | normal |
| Dashboard | ready | dark | normal |
| Dashboard | ready | light | compact |
| Dashboard | degraded | light | normal |
| Detail Page | ready | light | normal |
| Detail Page | error | light | normal |
| Detail Page | permission-required | dark | normal |
| Settings Center | ready | light | normal |
| Settings Center | conflict | light | normal |
| Command Palette | empty | dark | normal |
| Command Palette | results | dark | normal |
| Logs Viewer | streaming | light | normal |

## 分辨率

```text
1366x768
1440x900
1920x1080
2560x1440
```

## 命令

```text
mpt ui snapshot --surface dashboard-card --theme light --size 1920x1080
mpt ui check --baseline tests/ui/baseline
```

## 失败处理

```text
输出 diff image
输出 changed bounding boxes
输出 token usage report
输出 component usage report
```
