import SwiftUI
import AppKit

@MainActor final class AppModel: ObservableObject {
    @Published var code = PairingStore.load()
    @Published var status = "Paste the pairing code from your PC to get started."
    @Published var audioStatus = "Audio is independent of keyboard and monitor switching."
    @Published var running = false
    @Published var connected = false
    @Published var sendAudio = UserDefaults.standard.object(forKey: "sendAudio") as? Bool ?? true
    @Published var routes: [DisplayRoute] = []
    private let link = Connection()
    private let audio = AudioCapture()
    private var resumeAfterWake = false
    init() {
        if let bytes = UserDefaults.standard.data(forKey: "displays"), let saved = try? JSONDecoder().decode([DisplayRoute].self, from: bytes) { routes = saved }
        link.report = { [weak self] text in
            NSLog("TwinDesk connection: %@", text)
            DispatchQueue.main.async { self?.status = text }
        }
        link.state = { [weak self] connected in DispatchQueue.main.async {
            guard let self else { return }; self.connected = connected
            Task { if connected && self.sendAudio { await self.startAudio() } else { await self.audio.stop() } }
        } }
        let connection = link
        audio.send = { bytes in connection.sendAudio(bytes) }
        audio.report = { [weak self] text in DispatchQueue.main.async { self?.audioStatus = text } }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.resumeAfterWake = self.running
                self.running = false; self.connected = false
                self.link.stop(); await self.audio.stop()
            }
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.resumeAfterWake else { return }
                self.resumeAfterWake = false
                self.connect()
            }
        }
        NSLog("TwinDesk startup: Accessibility=%@", AXIsProcessTrusted() ? "granted" : "missing")
        if !code.isEmpty { DispatchQueue.main.async { [weak self] in self?.connect() } }
    }
    func connect() {
        do {
            let pairing = try Pairing.parse(code); try PairingStore.save(code)
            UserDefaults.standard.set(try JSONEncoder().encode(routes), forKey: "displays")
            running = true; link.start(pairing, routes: routes); status = "Connecting…"
        } catch { status = error.localizedDescription }
    }
    func disconnect() { resumeAfterWake = false; running = false; connected = false; link.stop(); Task { await audio.stop() } }
    func returnToWindows() { link.returnToWindows() }
    func scan() {
        status = "Reading connected monitors…"
        Task {
            do {
                let found = try await Task.detached { try Displays.scan() }.value
                routes = found.map { route in var result = route; result.pcInput = routes.first(where: { $0.id == route.id })?.pcInput ?? 15; return result }
                status = "Found \(routes.count) monitor(s). Set their Windows cable inputs below."
            } catch { status = error.localizedDescription }
        }
    }
    func startAudio() async {
        do { try await audio.start(); if !connected || !sendAudio { await audio.stop() } }
        catch { audioStatus = "Audio permission or capture failed: " + error.localizedDescription }
    }
    func audioChanged() {
        UserDefaults.standard.set(sendAudio, forKey: "sendAudio")
        Task { if sendAudio && connected { await startAudio() } else { await audio.stop() } }
    }
    func accessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    func openDrive(_ drive: String) {
        do { let pairing = try Pairing.parse(code); NSWorkspace.shared.open(URL(string: "smb://\(pairing.host)/TwinDesk-\(drive)")!) }
        catch { status = "Add your PC pairing code first." }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var cleanup: (() -> Void)?
    private var startupWindowHidden = false
    func hideStartupWindowOnce() {
        guard !startupWindowHidden, ProcessInfo.processInfo.arguments.contains("--startup") else { return }
        startupWindowHidden = true
        DispatchQueue.main.async {
            NSApp.setActivationPolicy(.accessory)
            NSApp.hide(nil)
        }
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        cleanup?()
        DispatchQueue.main.asyncAfter(deadline: .now() + 7) { sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}

@main @MainActor struct TwinDeskApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = AppModel()
    init() {
        if ProcessInfo.processInfo.arguments.contains("--diagnostics") {
            let saved = PairingStore.load()
            let pairing = try? Pairing.parse(saved)
            let info: [String: Any] = [
                "accessibilityTrusted": AXIsProcessTrusted(),
                "audioBackend": "Core Audio process tap (no screen capture)",
                "pairingSaved": pairing != nil,
                "pairedHost": pairing?.host ?? "",
                "pairedPort": Int(pairing?.port ?? 0)
            ]
            if let data = try? JSONSerialization.data(withJSONObject: info, options: [.sortedKeys]),
               let text = String(data: data, encoding: .utf8) { print(text) }
            exit(0)
        }
    }
    var body: some Scene {
        WindowGroup("TwinDesk") { ContentView(model: model).onAppear { delegate.cleanup = { model.disconnect() }; delegate.hideStartupWindowOnce() } }.defaultSize(width: 660, height: 610)
            .commands {
                CommandGroup(after: .appInfo) {
                    Button("Switch back to Windows") { model.returnToWindows() }.disabled(!model.connected)
                    Divider()
                }
            }
        MenuBarExtra("TwinDesk", systemImage: "desktopcomputer") {
            Text(model.connected ? "Connected to PC" : "Disconnected")
            Button("Switch back to Windows") { model.returnToWindows() }.disabled(!model.connected)
            Divider()
            Button("Open TwinDesk") {
                NSApp.unhide(nil)
                NSApp.windows.first(where: { $0.title.contains("TwinDesk") })?.makeKeyAndOrderFront(nil)
                NSApp.activate(ignoringOtherApps: true)
            }
            Button("Open C: drive") { model.openDrive("C") }
            Button("Open E: drive") { model.openDrive("E") }
            Divider()
            Button("Disconnect") { model.disconnect() }
            Button("Quit TwinDesk") { NSApplication.shared.terminate(nil) }
        }
    }
}

@MainActor struct ContentView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("T W I N D E S K").font(.caption).foregroundStyle(.mint)
            Text(model.connected ? "Your PC and Mac, together." : "Connect your Mac.").font(.largeTitle.bold())
            Text(model.status).foregroundStyle(.secondary).textSelection(.enabled)
            Text("PC pairing code").font(.headline)
            SecureField("Paste the code copied from TwinDesk on Windows", text: $model.code).textFieldStyle(.roundedBorder).disabled(model.running)
            HStack {
                Button(model.running ? "Disconnect" : "Connect") { if model.running { model.disconnect() } else { model.connect() } }.buttonStyle(.borderedProminent)
                Button("Allow keyboard & mouse") { model.accessibility() }
                Button("Refresh monitors") { model.scan() }.disabled(model.running)
            }
            Toggle("Play Mac system audio through the PC", isOn: $model.sendAudio).onChange(of: model.sendAudio) { _ in model.audioChanged() }
            Text(model.audioStatus).font(.callout).foregroundStyle(.secondary)
            Divider()
            Text("Monitor return inputs").font(.headline)
            ForEach($model.routes) { $route in
                HStack {
                    Text(route.name).lineLimit(1)
                    Spacer()
                    Picker("PC cable", selection: $route.pcInput) { Text("DisplayPort").tag(15); Text("HDMI 1").tag(17); Text("HDMI 2").tag(18) }.frame(width: 230).disabled(model.running)
                }
            }
            Text("Ctrl + Alt + F12 switches computers. Ctrl + Alt + F11 returns to Windows. Windows key acts as Command on the Mac.").font(.callout).foregroundStyle(.secondary)
            Divider()
            HStack { Text("Windows files").font(.headline); Spacer(); Button("Open C: drive") { model.openDrive("C") }; Button("Open E: drive") { model.openDrive("E") } }
            Text("Use your Windows username and password when Finder asks. File access remains available while TwinDesk is disconnected.").font(.callout).foregroundStyle(.secondary)
            Spacer(minLength: 0)
        }.padding(28).frame(minWidth: 620, minHeight: 580).preferredColorScheme(.dark)
    }
}
