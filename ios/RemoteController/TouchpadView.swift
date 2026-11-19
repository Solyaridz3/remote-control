import SwiftUI
import UIKit

struct TouchSurface: UIViewRepresentable {
    var onMove: (CGFloat, CGFloat) -> Void
    var onScroll: (CGFloat) -> Void
    var onClick: (Bool) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.backgroundColor = .clear
        view.isMultipleTouchEnabled = true
        view.isAccessibilityElement = true
        view.accessibilityLabel = "Тачпад ноутбука"
        view.accessibilityHint = "Проведите для движения курсора. Касание — клик. Два пальца — прокрутка или правый клик."
        view.accessibilityTraits = [.allowsDirectInteraction]
        let pan = UIPanGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.pan(_:)))
        pan.minimumNumberOfTouches = 1
        pan.maximumNumberOfTouches = 2
        let tap = UITapGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.tap(_:)))
        let rightTap = UITapGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.rightTap(_:)))
        rightTap.numberOfTouchesRequired = 2
        tap.require(toFail: rightTap)
        view.addGestureRecognizer(pan)
        view.addGestureRecognizer(tap)
        view.addGestureRecognizer(rightTap)
        return view
    }
    func updateUIView(_ uiView: UIView, context: Context) { context.coordinator.parent = self }

    final class Coordinator: NSObject {
        var parent: TouchSurface
        var previous = CGPoint.zero
        var fingers = 0
        init(_ parent: TouchSurface) { self.parent = parent }
        @objc func pan(_ gesture: UIPanGestureRecognizer) {
            let location = gesture.translation(in: gesture.view)
            let count = gesture.numberOfTouches
            if gesture.state == .began || count != fingers {
                previous = location
                fingers = count
                return
            }
            if gesture.state == .changed {
                let dx = location.x - previous.x, dy = location.y - previous.y
                if count == 1 { parent.onMove(dx, dy) }
                else if count == 2 { parent.onScroll(dy) }
                previous = location
            }
            if gesture.state == .ended || gesture.state == .cancelled || gesture.state == .failed {
                fingers = 0
                previous = .zero
            }
        }
        @objc func tap(_ gesture: UITapGestureRecognizer) { parent.onClick(false) }
        @objc func rightTap(_ gesture: UITapGestureRecognizer) { parent.onClick(true) }
    }
}

struct TouchpadView: View {
    @ObservedObject var client: RemoteClient
    private let mint = Color(red: 0.48, green: 0.93, blue: 0.78)

    var body: some View {
        ZStack {
            Color(red: 0.08, green: 0.11, blue: 0.15)
            Canvas { context, size in
                for x in stride(from: 18.0, to: size.width, by: 22) {
                    for y in stride(from: 18.0, to: size.height, by: 22) {
                        context.fill(Path(ellipseIn: CGRect(x: x, y: y, width: 2, height: 2)), with: .color(.white.opacity(0.10)))
                    }
                }
            }
            VStack(spacing: 12) {
                Image(systemName: client.connected ? "cursorarrow.motionlines" : "hand.draw")
                    .font(.system(size: 34, weight: .light))
                Text(client.connected ? "ДВИГАЙТЕ ПАЛЬЦЕМ" : "ВАШ ТАЧПАД")
                    .font(.system(size: 11, weight: .semibold, design: .monospaced)).tracking(2)
            }
            .foregroundStyle(client.connected ? mint : .white.opacity(0.45))
            .allowsHitTesting(false)
            VStack {
                HStack {
                    Text("REMOTE / 01")
                    Spacer()
                    Image(systemName: "wifi")
                }
                Spacer()
                Text("Касание — клик   ·   Два пальца — прокрутка")
                    .font(.system(size: 11))
            }
            .font(.system(size: 10, design: .monospaced))
            .foregroundStyle(.white.opacity(0.4))
            .padding(20)
            .allowsHitTesting(false)
            TouchSurface(onMove: { client.move(dx: $0, dy: $1) },
                         onScroll: { client.scroll(dy: $0) },
                         onClick: { client.click(right: $0) })
                .allowsHitTesting(client.connected)
        }
        .aspectRatio(1, contentMode: .fit)
        .overlay(alignment: .bottom) { Rectangle().fill(mint.opacity(0.35)).frame(height: 1) }
    }
}
