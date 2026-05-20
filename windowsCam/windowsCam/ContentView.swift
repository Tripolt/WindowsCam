import AVFoundation
import SwiftUI

struct ContentView: View {
    @StateObject private var camera = CameraPipeline()
    @State private var showSettings = false
    @Environment(\.verticalSizeClass) private var verticalSizeClass

    private var isLandscape: Bool {
        verticalSizeClass == .compact
    }

    var body: some View {
        ZStack {
            CameraPreviewView(session: camera.session)
                .ignoresSafeArea()

            if isLandscape {
                landscapeOverlay
            } else {
                portraitOverlay
            }
        }
        .preferredColorScheme(.dark)
        .task {
            await camera.requestAccessAndStart()
        }
        .animation(.easeInOut(duration: 0.3), value: showSettings)
        .animation(.easeInOut(duration: 0.3), value: isLandscape)
        .animation(.easeInOut(duration: 0.2), value: camera.isRunning)
    }

    // ──────────────────────────────────────────────
    // MARK: - Portrait Layout
    // ──────────────────────────────────────────────

    private var portraitOverlay: some View {
        VStack(spacing: 0) {
            // Top status bar
            GlassEffectContainer(spacing: 6) {
                HStack {
                    statusChip(
                        icon: camera.isRunning ? "circle.fill" : "circle",
                        text: camera.captureStatus,
                        tint: camera.isRunning ? .green : .gray
                    )
                    Spacer()
                    statusChip(
                        icon: "network",
                        text: camera.streamStatus,
                        tint: .cyan
                    )
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)

            Spacer()

            // Settings panel (slides up)
            if showSettings {
                settingsPanel
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .padding(.bottom, 12)
            }

            // Bottom control bar
            portraitControlBar
        }
    }

    // ──────────────────────────────────────────────
    // MARK: - Landscape Layout
    // ──────────────────────────────────────────────

    private var landscapeOverlay: some View {
        HStack(spacing: 0) {
            // Left: status chips
            GlassEffectContainer(spacing: 6) {
                VStack(alignment: .leading, spacing: 8) {
                    statusChip(
                        icon: camera.isRunning ? "circle.fill" : "circle",
                        text: camera.captureStatus,
                        tint: camera.isRunning ? .green : .gray
                    )
                    statusChip(
                        icon: "network",
                        text: camera.streamStatus,
                        tint: .cyan
                    )
                }
            }
            .padding(.leading, 16)
            .padding(.top, 12)
            .frame(maxHeight: .infinity, alignment: .top)

            Spacer()

            // Right: controls sidebar
            landscapeSidebar
        }
    }

    // ──────────────────────────────────────────────
    // MARK: - Portrait Control Bar
    // ──────────────────────────────────────────────

    private var portraitControlBar: some View {
        GlassEffectContainer(spacing: 10) {
            // Use ZStack so the main button is always perfectly centered,
            // regardless of the widths of the flanking elements.
            ZStack {
                // Center: main action button
                mainActionButton

                // Left: settings gear  |  Right: config badge
                HStack {
                    settingsButton
                    Spacer()
                    configBadge
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 14)
        }
        .padding(.horizontal, 8)
        .padding(.bottom, 4)
    }

    // ──────────────────────────────────────────────
    // MARK: - Landscape Sidebar
    // ──────────────────────────────────────────────

    private var landscapeSidebar: some View {
        HStack(spacing: 10) {
            // Settings panel (appears to the left of controls)
            if showSettings {
                GlassEffectContainer(spacing: 8) {
                    landscapeSettingsPanel
                }
                .frame(width: 190)
                .transition(.move(edge: .trailing).combined(with: .opacity))
            }

            // Controls strip — always on the right edge, vertically centered
            GlassEffectContainer(spacing: 10) {
                VStack(spacing: 14) {
                    configBadge
                    mainActionButton
                    settingsButton
                }
                .padding(.vertical, 16)
                .padding(.horizontal, 10)
            }
        }
        .padding(.trailing, 10)
        .padding(.vertical, 8)
        .frame(maxHeight: .infinity)
    }

    // ──────────────────────────────────────────────
    // MARK: - Main Action Button
    // ──────────────────────────────────────────────

    private var mainActionButton: some View {
        Button {
            if camera.isRunning {
                camera.stop()
            } else {
                camera.start()
            }
        } label: {
            Image(systemName: camera.isRunning ? "stop.fill" : "play.fill")
                .font(.title2)
                .foregroundStyle(.white)
                .frame(width: 64, height: 64)
        }
        .glassEffect(
            .regular.interactive().tint(camera.isRunning ? .red : .green),
            in: Circle()
        )
    }

    // ──────────────────────────────────────────────
    // MARK: - Settings Button
    // ──────────────────────────────────────────────

    private var settingsButton: some View {
        Button {
            showSettings.toggle()
        } label: {
            Image(systemName: "gearshape.fill")
                .font(.body)
                .foregroundStyle(.white)
                .frame(width: 42, height: 42)
                .rotationEffect(.degrees(showSettings ? 90 : 0))
        }
        .glassEffect(.regular.interactive(), in: Circle())
    }

    // ──────────────────────────────────────────────
    // MARK: - Settings Panel (Portrait)
    // ──────────────────────────────────────────────

    private var settingsPanel: some View {
        GlassEffectContainer(spacing: 8) {
            VStack(spacing: 16) {
                HStack {
                    Label("Camera Output", systemImage: "arrow.up.right.video")
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(.white)
                    Spacer()
                    Button {
                        showSettings = false
                    } label: {
                        Image(systemName: "xmark")
                            .font(.caption.weight(.bold))
                            .foregroundStyle(.white.opacity(0.6))
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)
                    .glassEffect(.clear.interactive(), in: Circle())
                }

                // Resolution
                VStack(alignment: .leading, spacing: 8) {
                    Label("Resolution", systemImage: "rectangle.on.rectangle")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)

                    HStack(spacing: 8) {
                        ForEach(OutputResolution.allCases) { res in
                            resolutionChip(res)
                        }
                    }
                }

                // Frame rate
                VStack(alignment: .leading, spacing: 8) {
                    Label("Frame Rate", systemImage: "speedometer")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)

                    HStack(spacing: 8) {
                        ForEach(OutputFrameRate.allCases) { fps in
                            frameRateChip(fps)
                        }
                    }
                }
            }
            .padding(16)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 20))
        }
        .padding(.horizontal, 16)
    }

    // ──────────────────────────────────────────────
    // MARK: - Settings Panel (Landscape)
    // ──────────────────────────────────────────────

    private var landscapeSettingsPanel: some View {
        VStack(spacing: 12) {
            HStack {
                Text("Camera Output")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white)
                Spacer()
                Button {
                    showSettings = false
                } label: {
                    Image(systemName: "xmark")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(.white.opacity(0.6))
                        .frame(width: 24, height: 24)
                }
                .buttonStyle(.plain)
                .glassEffect(.clear.interactive(), in: Circle())
            }

            // Resolution
            VStack(alignment: .leading, spacing: 6) {
                Label("Resolution", systemImage: "rectangle.on.rectangle")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.secondary)

                VStack(spacing: 6) {
                    ForEach(OutputResolution.allCases) { res in
                        resolutionChipLandscape(res)
                    }
                }
            }

            // Frame rate
            VStack(alignment: .leading, spacing: 6) {
                Label("Frame Rate", systemImage: "speedometer")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.secondary)

                VStack(spacing: 6) {
                    ForEach(OutputFrameRate.allCases) { fps in
                        frameRateChip(fps)
                    }
                }
            }
        }
        .padding(12)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 16))
    }

    // ──────────────────────────────────────────────
    // MARK: - Reusable Components
    // ──────────────────────────────────────────────

    // Status chip — compact capsule with colored icon
    private func statusChip(icon: String, text: String, tint: Color) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon)
                .font(.caption2)
                .foregroundStyle(tint)
            Text(text)
                .font(.caption2.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .glassEffect(.clear, in: Capsule())
    }

    // Config badge — always-visible current-settings indicator
    private var configBadge: some View {
        Text("\(camera.selectedResolution.label) · \(camera.selectedFrameRate.label)")
            .font(.caption2.weight(.bold).monospacedDigit())
            .foregroundStyle(.white.opacity(0.85))
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .glassEffect(.clear, in: Capsule())
    }

    // Resolution chip (portrait — horizontal row)
    private func resolutionChip(_ res: OutputResolution) -> some View {
        let selected = camera.selectedResolution == res
        return Button {
            camera.selectedResolution = res
        } label: {
            Text(res.label)
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .padding(.horizontal, 14)
                .padding(.vertical, 9)
                .frame(maxWidth: .infinity)
        }
        .glassEffect(
            selected
                ? .regular.interactive().tint(.accentColor)
                : .clear,
            in: RoundedRectangle(cornerRadius: 10)
        )
        .buttonStyle(.plain)
    }

    // Resolution chip (landscape — vertical list with dimensions)
    private func resolutionChipLandscape(_ res: OutputResolution) -> some View {
        let selected = camera.selectedResolution == res
        return Button {
            camera.selectedResolution = res
        } label: {
            HStack {
                Text(res.label)
                    .font(.caption.weight(.bold))
                Spacer()
                Text("\(res.nominalWidth)×\(res.nominalHeight)")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity)
        }
        .glassEffect(
            selected
                ? .regular.interactive().tint(.accentColor)
                : .clear,
            in: RoundedRectangle(cornerRadius: 10)
        )
        .buttonStyle(.plain)
    }

    // Frame rate chip (shared between both orientations)
    private func frameRateChip(_ fps: OutputFrameRate) -> some View {
        let selected = camera.selectedFrameRate == fps
        return Button {
            camera.selectedFrameRate = fps
        } label: {
            Text(fps.label)
                .font(.caption.weight(.bold))
                .foregroundStyle(.white)
                .padding(.horizontal, 14)
                .padding(.vertical, 9)
                .frame(maxWidth: .infinity)
        }
        .glassEffect(
            selected
                ? .regular.interactive().tint(.accentColor)
                : .clear,
            in: RoundedRectangle(cornerRadius: 10)
        )
        .buttonStyle(.plain)
    }
}
