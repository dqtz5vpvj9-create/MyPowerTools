import AppKit
import SwiftUI

/// 设置页：与看板统一的卡片风格，按类别分组（疲劳提醒 / 声音 / 隐私 / 采集 / 应用分类）
struct SettingsView: View {
    @ObservedObject var settings: SettingsStore
    let appState: AppState

    /// 观察分类映射：用户改选分类后 Picker 立即刷新显示
    @ObservedObject private var categoryMap = AppCategoryMap.shared

    @State private var knownApps: [AppUsageSummary] = []

    private let soundOptions = ["Glass", "Hero", "Ping", "Blow", "Bottle", "Frog", "Funk", "Morse", "Pop", "Purr", "Sosumi", "Submarine", "Tink"]

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                generalCard
                fatigueCard
                soundCard
                privacyCard
                captureCard
                categoryCard
            }
            .padding(20)
        }
        .background(DashboardTheme.pageBackground)
        .onAppear { loadKnownApps() }
    }

    // MARK: - 通用

    private var generalCard: some View {
        SettingsCard(title: "通用", icon: "gearshape") {
            Toggle("开机自动启动", isOn: $settings.launchAtLogin)
            HStack {
                Text("数据保存周期")
                    .font(.system(size: 12))
                TextField("天", value: $settings.dataRetentionDays, format: .number.grouping(.never))
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.trailing)
                    .frame(width: 72)
                Text("天")
                    .font(.system(size: 12))
                Stepper("", value: $settings.dataRetentionDays, in: 1...36500, step: 1)
                    .labelsHidden()
                Text("默认 365 天（1 年）")
                    .font(.system(size: 11))
                    .foregroundColor(.secondary)
            }
            .onChange(of: settings.dataRetentionDays) { newValue in
                appState.repository?.purgeExpiredData(retentionDays: newValue)
            }
            Text("超出保存周期的历史数据会自动清理（启动时及修改本设置后触发）。")
                .font(.system(size: 11))
                .foregroundColor(.secondary)
        }
    }

    // MARK: - 疲劳提醒

    private var fatigueCard: some View {
        SettingsCard(title: "疲劳提醒", icon: "bell.badge") {
            VStack(alignment: .leading, spacing: 4) {
                Text("提醒间隔：连续活动 \(Int(settings.remindIntervalMinutes)) 分钟达到 100%")
                    .font(.system(size: 12))
                Slider(value: $settings.remindIntervalMinutes, in: 5...120, step: 5)
            }
            HStack {
                Text("休息时长")
                    .font(.system(size: 12))
                TextField("秒", value: $settings.restDurationSeconds, format: .number.grouping(.never))
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.trailing)
                    .frame(width: 72)
                Text("秒")
                    .font(.system(size: 12))
                Stepper("", value: $settings.restDurationSeconds, in: 10...7200, step: 10)
                    .labelsHidden()
                Text("≈ \(restDurationText)")
                    .font(.system(size: 11))
                    .foregroundColor(.secondary)
            }
            Toggle("键盘活动计入疲劳", isOn: $settings.fatigueFromKeyboard)
            Toggle("鼠标活动计入疲劳", isOn: $settings.fatigueFromMouse)
            Toggle("窗口活动计入疲劳", isOn: $settings.fatigueFromApp)
            Toggle("暂停提醒恢复后，若已超阈值立即提醒", isOn: $settings.remindAfterResume)
        }
    }

    // MARK: - 提醒声音

    private var soundCard: some View {
        SettingsCard(title: "提醒声音", icon: "speaker.wave.2") {
            Toggle("休息结束播放提示音", isOn: $settings.soundEnabled)
            HStack {
                Text("音效")
                    .font(.system(size: 12))
                Picker("", selection: $settings.soundName) {
                    ForEach(soundOptions, id: \.self) { Text($0).tag($0) }
                }
                .labelsHidden()
                .frame(width: 140)
                Button("试听") { playSound() }
            }
            HStack {
                Text("音量")
                    .font(.system(size: 12))
                Slider(value: $settings.soundVolume, in: 0...1)
            }
        }
    }

    // MARK: - 隐私

    private var privacyCard: some View {
        SettingsCard(title: "隐私", icon: "lock.shield") {
            Toggle("隐私模式（只记录按键次数，不记录按键内容）", isOn: $settings.privacyMode)
                .onChange(of: settings.privacyMode) { newValue in
                    appState.eventTapEngine.privacyMode = newValue
                }
            Text("密码输入期间系统自动屏蔽键盘采集；所有数据仅保存在本机，应用不联网。")
                .font(.system(size: 11))
                .foregroundColor(.secondary)
        }
    }

    // MARK: - 采集

    private var captureCard: some View {
        SettingsCard(title: "采集", icon: "waveform.path.ecg") {
            VStack(alignment: .leading, spacing: 4) {
                Text("轨迹采样最小距离：\(Int(settings.trackSampleDistance)) px")
                    .font(.system(size: 12))
                Slider(value: $settings.trackSampleDistance, in: 10...100, step: 5)
                    .onChange(of: settings.trackSampleDistance) { newValue in
                        appState.cursorPoller.updateSampleDistance(newValue)
                    }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("窗口活动心跳间隔：\(Int(settings.appHeartbeatSeconds)) 秒")
                    .font(.system(size: 12))
                Slider(value: $settings.appHeartbeatSeconds, in: 10...120, step: 5)
                    .onChange(of: settings.appHeartbeatSeconds) { newValue in
                        appState.frontAppTracker.updateHeartbeatInterval(newValue)
                    }
            }
            Text("所有设置调整均立即生效。")
                .font(.system(size: 11))
                .foregroundColor(.secondary)
        }
    }

    // MARK: - 应用分类

    private var categoryCard: some View {
        SettingsCard(title: "应用分类", icon: "square.grid.2x2") {
            if knownApps.isEmpty {
                Text("暂无应用使用记录")
                    .font(.system(size: 12))
                    .foregroundColor(.secondary)
                    .padding(.vertical, 8)
                    .frame(maxWidth: .infinity)
            } else {
                ForEach(knownApps.prefix(20), id: \.bundleID) { app in
                    HStack {
                        Text(app.appName)
                            .font(.system(size: 12))
                            .lineLimit(1)
                        Spacer()
                        Picker("", selection: categoryBinding(for: app.bundleID)) {
                            ForEach(AppCategory.allCases, id: \.self) { c in
                                Text(c.displayName).tag(c)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 110)
                    }
                }
                Text("分类调整立即生效，历史统计同步按新分类计算。")
                    .font(.system(size: 11))
                    .foregroundColor(.secondary)
            }
        }
    }

    // MARK: - 逻辑

    private func categoryBinding(for bundleID: String) -> Binding<AppCategory> {
        Binding(
            get: { self.categoryMap.category(for: bundleID) },
            set: { self.categoryMap.setOverride($0, for: bundleID) }
        )
    }

    /// 休息时长友好展示（如 "5分30秒"）
    private var restDurationText: String {
        let total = settings.restDurationSeconds
        let m = total / 60
        let s = total % 60
        if m == 0 { return "\(s)秒" }
        if s == 0 { return "\(m)分钟" }
        return "\(m)分\(s)秒"
    }

    private func playSound() {
        guard let sound = NSSound(named: NSSound.Name(settings.soundName)) else { return }
        sound.volume = Float(settings.soundVolume)
        sound.play()
    }

    private func loadKnownApps() {
        guard let repo = appState.repository else { return }
        let end = Date().timeIntervalSince1970
        let start = end - 30 * 86400
        DispatchQueue.global(qos: .userInitiated).async {
            let apps = repo.appUsage(from: start, to: end)
            DispatchQueue.main.async { self.knownApps = apps }
        }
    }
}

/// 设置分类卡片（与看板统一风格）
private struct SettingsCard<Content: View>: View {
    let title: String
    let icon: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(title, systemImage: icon)
                .font(.system(size: 13, weight: .semibold))
            content
                .font(.system(size: 12))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(16)
        .dashboardCard()
    }
}
