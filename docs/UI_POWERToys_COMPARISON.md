# UI PowerToys Comparison

Run date: 2026-07-06.

This comparison uses Microsoft PowerToys as a product-quality reference for dense Windows utility UI: persistent navigation, predictable settings surfaces, command launcher as overlay, clear module state, and low-drama operational pages.

| Area | PowerToys Pattern | MyPowerTools Before | Final P-UI-Foundation State | Future Enhancement |
| --- | --- | --- | --- | --- |
| Navigation | Stable left navigation with compact product identity and clear selected page. | Sidebar competed with a permanent command rail and duplicated title. | Sidebar is stable, brand is compact `MPT`, pages are Dashboard/Modules/Commands/Settings/Logs/Notifications/Packages/Diagnostics. | Add stronger selected-state styling without extra text. |
| Dashboard | Summary state first, then module cards/actions. | Dashboard was squeezed by a permanent right rail. | Dashboard has Runner badge, four metrics, runtime policy banner, responsive module grid, status badges, and primary actions. | Add richer empty/degraded variants. |
| Command launcher | Overlay experience, keyboard-first, separate from dashboard. | Command Palette was docked as a constant right panel. | Commands open as centered overlay from nav, topbar, and keyboard with params, progress, result, and cancellation evidence. | Add stronger focus/selection styling. |
| Settings | Schema-backed fields with dirty state and save feedback. | Existing schema editor was functional but visually rough. | Settings remains schema-backed with staged diff and MPT input/action controls. | Add richer field grouping for very large schemas. |
| Logs | Long-line-safe viewer with severity and module filter. | Logs existed but depended on rough page styling. | Logs wrap long lines and keep module selection. | Add toolbar search/filter controls and copy/export actions. |
| Notifications | Timeline/list with readable severity state. | Basic notification list. | Notification list is clean and full-width. | Add bulk clear, severity filters, and empty state polish. |
| Packages | Package operation section plus package cards/actions. | Package link text could be clipped. | Package module-link truncation is removed; operations and cards are usable. | Add trust badge styling and clearer destructive-action hierarchy. |
| Diagnostics | Dense runtime facts, process controls, and audit history. | Diagnostics page existed. | Diagnostics presents metrics, paths, transports, processes, histories, and broker audit. | Add tabs/sections for scanning large diagnostics sets. |
| Theming | Light/dark surfaces remain legible. | Dark screenshot used light resources. | Dark screenshot uses dark background/surfaces/text resources. | Apply the palette to runtime theme switching, not only screenshot generation. |
| Evidence | Screenshots and manifests prove UI behavior. | Evidence did not fail semantic UI regressions. | `ui screenshot` alias, live Runner manifest, dark/compact/1920 fixture matrix, semantic UI gate, final evidence package, and final source/docs package exist. | Add CI-hosted visual diff publication. |
