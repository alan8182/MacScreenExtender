#!/usr/bin/env python3
"""
Self-check script for Windows receiver GPU rendering pipeline invariants.
Ensures critical wiring and bug fixes remain intact.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent

def check_receiver_form():
    content = (ROOT / "ReceiverForm.cs").read_text(encoding="utf-8")
    assert "_gpu.SetFrameSource(_dec);" in content, "ReceiverForm: _gpu.SetFrameSource(_dec) is missing!"
    assert "new InputForwarder(ControlActive, SendPacket, _screenW, _screenH, ChannelHealthy)" in content, (
        "ReceiverForm: ChannelHealthy missing from InputForwarder!"
    )
    assert "Checked = true" in content and "public bool Gpu { get; set; } = true;" in content, (
        "ReceiverForm: GPU mode must be enabled by default!"
    )
    assert "ToggleOsd()" in content and "_osdTimer" in content, (
        "ReceiverForm: OSD toggle and auto-hide timer must be implemented!"
    )
    assert "Keys.F11" in content and "Keys.F1" in content, (
        "ReceiverForm: F11 fullscreen and F1 OSD shortcuts must be handled!"
    )
    assert "_dec.Width == targetW * 2" in content, (
        "ReceiverForm: HiDPI watchdog must recognize 2x Retina resolution with scaling without false reconnects!"
    )
    assert "_scaleCombo" in content and "ScalePercent" in content, (
        "ReceiverForm: ScalePercent and _scaleCombo UI must be implemented!"
    )
    assert "CardPanel" in content and "ModernButton" in content, (
        "ReceiverForm: Modern macOS card and button controls must be present!"
    )
    
    # Check InputForwarder hook allows Esc, F1, F11 through
    fwd_content = (ROOT / "InputForwarder.cs").read_text(encoding="utf-8")
    assert "0x70" in fwd_content and "0x7A" in fwd_content, (
        "InputForwarder: F1 (0x70) and F11 (0x7A) must be passed through to Windows!"
    )
    print("✓ ReceiverForm.cs and InputForwarder.cs checks passed")

def check_video_decoder():
    content = (ROOT / "VideoDecoder.cs").read_text(encoding="utf-8")
    assert "_getFormatFunc = GetFormat;" in content, "VideoDecoder: _getFormatFunc initialization is missing!"
    assert "_dec->get_format = _getFormatFunc;" in content, "VideoDecoder: _dec->get_format assignment is missing!"
    assert "AVPixelFormat.AV_PIX_FMT_D3D11" in content, "VideoDecoder: AV_PIX_FMT_D3D11 return in GetFormat is missing!"
    assert "Renderer != null" in content, "VideoDecoder: D3D11VA hardware context must only attach when GPU Renderer is active!"
    
    # Check lock (_hwSync) around _retained in Open and CloseLocked
    open_retained = re.search(r"lock\s*\(_hwSync\)\s*\{\s*_retained\s*=\s*ffmpeg\.av_frame_alloc\(\);", content)
    assert open_retained, "VideoDecoder: _retained allocation in Open must be inside lock (_hwSync)!"

    close_retained = re.search(r"lock\s*\(_hwSync\)\s*\{\s*ret\s*=\s*_retained;\s*_retained\s*=\s*null;", content)
    assert close_retained, "VideoDecoder: _retained cleanup in CloseLocked must be inside lock (_hwSync)!"
    print("✓ VideoDecoder.cs checks passed")

def check_gpu_renderer():
    content = (ROOT / "GpuVideoRenderer.cs").read_text(encoding="utf-8")
    assert "Scaling = Scaling.Stretch" in content, "GpuVideoRenderer: Scaling.Stretch is missing!"
    assert "CreateProcEnum(int inW, int inH, int outW, int outH)" in content, (
        "GpuVideoRenderer: CreateProcEnum must distinguish input and output dimensions!"
    )
    assert "RecreateVideoProcessor()" in content, "GpuVideoRenderer: RecreateVideoProcessor method is missing!"
    assert "GC.SuppressFinalize(tex)" in content, "GpuVideoRenderer: GC.SuppressFinalize(tex) is missing!"
    assert "FrameLatencyWaitableObject" in content, "GpuVideoRenderer: FrameLatencyWaitableObject is missing!"
    assert "MaximumFrameLatency = 1" in content, "GpuVideoRenderer: MaximumFrameLatency = 1 is missing!"
    assert "WaitForSingleObject" in content, "GpuVideoRenderer: WaitForSingleObject is missing!"
    assert "_frameSignal.WaitOne" in content, "GpuVideoRenderer: _frameSignal.WaitOne event-driven loop is missing!"
    print("✓ GpuVideoRenderer.cs checks passed")

def check_mac_sender():
    mac_root = ROOT.parent / "Sources" / "MacScreenSender"
    
    # Opt 4: Zero-copy GPU capture (no CPU memcpy or stampCursor)
    cap_content = (mac_root / "CaptureService.swift").read_text(encoding="utf-8")
    assert "memcpy" not in cap_content, "CaptureService: CPU memcpy must not be present (zero-copy GPU path required)!"
    assert "stampCursor" not in cap_content, "CaptureService: Software cursor stamping must be removed!"
    assert "CMSampleBufferGetImageBuffer" in cap_content, "CaptureService: Must extract CVPixelBuffer directly from CMSampleBuffer!"
    
    # Opt 1: IDR on demand in ControlServer
    ctl_content = (mac_root / "ControlServer.swift").read_text(encoding="utf-8")
    assert "case 9:" in ctl_content, "ControlServer: Type 9 IDR packet handler missing!"
    assert "onRequestKeyframe" in ctl_content, "ControlServer: onRequestKeyframe callback missing!"

    # Opt 2: HiDPI in main.swift & VideoEncoder
    main_content = (mac_root / "main.swift").read_text(encoding="utf-8")
    assert "params[\"hidpi\"]" in main_content, "main.swift: hidpi parameter negotiation missing!"
    assert "createVirtualDisplay" in main_content and "hiDPI" in main_content, "main.swift: HiDPI virtual display creation missing!"

    enc_content = (mac_root / "VideoEncoder.swift").read_text(encoding="utf-8")
    assert "kVTCompressionPropertyKey_ColorPrimaries" in enc_content, "VideoEncoder: BT.709 ColorPrimaries missing!"
    assert "kVTCompressionPropertyKey_TransferFunction" in enc_content, "VideoEncoder: BT.709 TransferFunction missing!"
    print("✓ MacScreenSender checks passed (Opt 1, Opt 2, Opt 4)")

def main():
    try:
        check_receiver_form()
        check_video_decoder()
        check_gpu_renderer()
        check_mac_sender()
        print("\nAll 4 architectural optimization checks passed successfully!")
    except AssertionError as e:
        print(f"FAILED: {e}", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
