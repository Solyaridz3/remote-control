import SwiftUI

struct ContentView: View {
    @StateObject private var client = RemoteClient()
    @State private var showConnection = false
    @Environment(\.scenePhase) private var scenePhase
    private let mint = Color(red: 0.48, green: 0.93, blue: 0.78)

    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 0) {
                    TouchpadView(client: client)
                        .frame(width: geometry.size.width, height: geometry.size.width)
                    VStack(alignment: .leading, spacing: 20) {
                        HStack {
                            VStack(alignment: .leading, spacing: 5) {
                                Text("PocketPointer").font(.system(size: 27, weight: .semibold))
                                Text(client.connected ? client.computer : "iPhone → Windows")
                                    .font(.subheadline).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Label(client.connected ? "В сети" : "Офлайн", systemImage: "circle.fill")
                                .font(.caption).foregroundStyle(client.connected ? mint : .gray)
                        }
                        VStack(alignment: .leading, spacing: 10) {
                            Text("ГРОМКОСТЬ НОУТБУКА").font(.system(size: 10, weight: .bold)).tracking(1.5).foregroundStyle(.secondary)
                            HStack(spacing: 10) {
                                volumeButton("speaker.minus.fill", label: "Тише", action: "down")
                                volumeButton("speaker.slash.fill", label: "Вкл./выкл. звук", action: "mute")
                                volumeButton("speaker.plus.fill", label: "Громче", action: "up")
                            }
                            .disabled(!client.connected)
                        }
                        HStack(spacing: 12) {
                            Button { showConnection = true } label: {
                                Label(client.connecting ? "Подключение…" : "Подключить", systemImage: "link")
                                    .frame(maxWidth: .infinity).padding(.vertical, 15)
                            }
                            .foregroundStyle(.black).background(mint, in: RoundedRectangle(cornerRadius: 14))
                            .disabled(client.connected || client.connecting)
                            .opacity(client.connected || client.connecting ? 0.4 : 1)
                            Button { client.disconnect() } label: {
                                Image(systemName: "power").frame(width: 54, height: 50)
                            }
                            .background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 14))
                            .accessibilityLabel("Отключиться от ноутбука")
                            .disabled(!client.connected && !client.connecting)
                        }
                        Text(client.status).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(24)
                }
            }
            .scrollDisabled(geometry.size.height >= geometry.size.width + 340)
            .background(Color(red: 0.045, green: 0.06, blue: 0.085))
        }
        .sheet(isPresented: $showConnection) { connectionSheet }
        .onChange(of: client.connected) { ready in
            UIApplication.shared.isIdleTimerDisabled = ready
            if ready { showConnection = false }
        }
        .onChange(of: scenePhase) { phase in
            if phase == .background {
                client.disconnect()
                UIApplication.shared.isIdleTimerDisabled = false
            }
        }
    }

    private func volumeButton(_ icon: String, label: String, action: String) -> some View {
        Button { client.volume(action) } label: {
            Image(systemName: icon).font(.system(size: 21)).frame(maxWidth: .infinity).frame(height: 58)
        }
        .background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 14))
        .accessibilityLabel(label)
    }

    private var connectionSheet: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Например, 192.168.1.12", text: $client.host)
                        .keyboardType(.decimalPad).accessibilityLabel("IPv4 адрес ноутбука")
                    TextField("Порт", text: $client.port).keyboardType(.numberPad)
                    TextField("Код из окна Windows", text: $client.code)
                        .textInputAutocapitalization(.characters).autocorrectionDisabled()
                        .font(.system(.body, design: .monospaced))
                } header: { Text("Ноутбук") } footer: {
                    Text("Откройте PocketPointer на ноутбуке. Оба устройства должны быть в одной Wi-Fi сети. Разрешите приложению доступ к локальной сети.")
                }
                .disabled(client.connecting)
                Section {
                    Button { client.connect() } label: {
                        HStack { Text(client.connecting ? "Подключение…" : "Подключиться"); Spacer(); if client.connecting { ProgressView() } }
                    }.disabled(client.connecting)
                    if client.connecting { Button("Отменить", role: .cancel) { client.disconnect() } }
                    Text(client.status).font(.footnote).foregroundStyle(.secondary)
                }
                Section("Чувствительность тачпада") {
                    Slider(value: $client.sensitivity, in: 0.5...3, step: 0.1)
                        .accessibilityLabel("Чувствительность")
                    Text(String(format: "%.1f×", client.sensitivity)).foregroundStyle(.secondary)
                }
                Section {
                    Text("Касание одним пальцем — левый клик. Касание двумя — правый клик. Движение двумя пальцами — прокрутка.")
                    Text("Перед первым подключением установите сертификат PocketPointer по инструкции в Windows. Соединение защищено TLS. При сворачивании приложение отключается.")
                }.font(.footnote).foregroundStyle(.secondary)
            }
            .navigationTitle("Подключение")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Готово") { showConnection = false } } }
        }
        .tint(mint)
    }
}
