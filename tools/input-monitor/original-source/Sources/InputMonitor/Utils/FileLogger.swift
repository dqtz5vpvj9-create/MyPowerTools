import Foundation

/// 文件日志：同时写 NSLog 与本地 debug.log，便于在无控制台环境下诊断
/// 路径: ~/Library/Application Support/InputMonitor/debug.log（超过 512KB 自动截断）
enum FileLogger {
    private static let queue = DispatchQueue(label: "com.local.inputmonitor.logger")
    private static var fileHandle: FileHandle?

    static let logURL: URL = {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport
            .appendingPathComponent("InputMonitor", isDirectory: true)
            .appendingPathComponent("debug.log")
    }()

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    /// 初始化（截断过大日志）；在 AppState.start() 最早处调用
    static func bootstrap() {
        queue.sync {
            do {
                let dir = logURL.deletingLastPathComponent()
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                if let attrs = try? FileManager.default.attributesOfItem(atPath: logURL.path),
                   let size = attrs[.size] as? Int, size > 512 * 1024 {
                    try FileManager.default.removeItem(at: logURL)
                }
                if !FileManager.default.fileExists(atPath: logURL.path) {
                    FileManager.default.createFile(atPath: logURL.path, contents: nil)
                }
                fileHandle = try FileHandle(forWritingTo: logURL)
                fileHandle?.seekToEndOfFile()
            } catch {
                NSLog("[InputMonitor] FileLogger init failed: \(error.localizedDescription)")
            }
        }
        log("---- app launch (pid \(ProcessInfo.processInfo.processIdentifier)) ----")
    }

    static func log(_ message: String) {
        NSLog("[InputMonitor] %@", message)
        queue.async {
            let line = "\(formatter.string(from: Date())) \(message)\n"
            if let data = line.data(using: .utf8) {
                fileHandle?.write(data)
            }
        }
    }
}
