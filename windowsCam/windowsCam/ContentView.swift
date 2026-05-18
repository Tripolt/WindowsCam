import AVFoundation
import SwiftUI

struct ContentView: View {
    @StateObject private var camera = CameraPipeline()

    var body: some View {
        ZStack(alignment: .bottom) {
            CameraPreviewView(session: camera.session)
                .ignoresSafeArea()

            VStack(spacing: 10) {
                HStack {
                    statusPill(camera.captureStatus)
                    Spacer()
                    statusPill(camera.streamStatus)
                }

                HStack(spacing: 12) {
                    Button {
                        camera.start()
                    } label: {
                        Label("Start", systemImage: "play.fill")
                    }
                    .buttonStyle(.borderedProminent)
                    .disabled(camera.isRunning)

                    Button {
                        camera.stop()
                    } label: {
                        Label("Stop", systemImage: "stop.fill")
                    }
                    .buttonStyle(.bordered)
                    .disabled(!camera.isRunning)
                }
            }
            .padding()
            .background(.ultraThinMaterial)
        }
        .task {
            await camera.requestAccessAndStart()
        }
    }

    private func statusPill(_ text: String) -> some View {
        Text(text)
            .font(.caption.weight(.semibold))
            .lineLimit(1)
            .minimumScaleFactor(0.75)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(.black.opacity(0.55), in: Capsule())
            .foregroundStyle(.white)
    }
}
