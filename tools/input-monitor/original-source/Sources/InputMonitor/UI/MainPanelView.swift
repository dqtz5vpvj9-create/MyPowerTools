import SwiftUI

/// 主面板分类
enum PanelCategory: String, CaseIterable, Identifiable {
    case stats = "活动统计"
    case settings = "设置"

    var id: String { rawValue }

    var icon: String {
        switch self {
        case .stats: return "chart.bar.xaxis"
        case .settings: return "gearshape"
        }
    }
}

/// 主面板共享状态（菜单入口可指定打开哪个分类）
final class MainPanelState: ObservableObject {
    @Published var selection: PanelCategory = .stats
}

/// 统一主面板：侧栏分类 + 详情区（活动统计 / 设置）
struct MainPanelView: View {
    @ObservedObject var state: MainPanelState
    let statsViewModel: StatsViewModel?
    let settings: SettingsStore
    @ObservedObject var appState: AppState

    var body: some View {
        NavigationSplitView {
            List(PanelCategory.allCases, selection: $state.selection) { category in
                Label(category.rawValue, systemImage: category.icon)
                    .tag(category)
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 140, ideal: 150, max: 170)
        } detail: {
            switch state.selection {
            case .stats:
                if let viewModel = statsViewModel {
                    StatsWindowView(viewModel: viewModel, interactionSeconds: appState.todayInteractionSeconds)
                } else {
                    Text("存储初始化失败")
                        .foregroundColor(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            case .settings:
                SettingsView(settings: settings, appState: appState)
            }
        }
        .navigationSplitViewStyle(.balanced)
        .frame(minWidth: 1080, minHeight: 640)
    }
}
