import Foundation
import AVFoundation
import CoreMedia

/// 从 Mac 端拉取 /raw Annex-B 视频流并喂入 AVSampleBufferDisplayLayer 的硬件解码客户端
public final class VideoStreamClient: NSObject, ObservableObject, URLSessionDataDelegate {
    @Published public private(set) var isConnected = false
    @Published public private(set) var currentFPS: Int = 0
    @Published public private(set) var receivedFrames: Int = 0
    @Published public private(set) var statusText: String = "就绪"

    private var session: URLSession?
    private var dataTask: URLSessionDataTask?
    private let parser = NALUParser()
    private weak var displayLayer: AVSampleBufferDisplayLayer?

    private var frameCountInSecond = 0
    private var fpsTimer: Timer?
    private let processingQueue = DispatchQueue(label: "com.macscreen.videostream", qos: .userInteractive)

    public init(displayLayer: AVSampleBufferDisplayLayer? = nil) {
        self.displayLayer = displayLayer
        super.init()
    }

    public func setDisplayLayer(_ layer: AVSampleBufferDisplayLayer) {
        self.displayLayer = layer
    }

    public func startStream(macIP: String,
                            port: Int = 8080,
                            token: String,
                            physicalWidth: Int,
                            physicalHeight: Int,
                            scalePercent: Int,
                            preferHEVC: Bool = true) {
        stopStream()

        parser.codec = preferHEVC ? .hevc : .h264
        parser.reset()

        let urlStr = "http://\(macIP):\(port)/raw?pw=\(physicalWidth)&ph=\(physicalHeight)&scale=\(scalePercent)&token=\(token.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "")"
        guard let url = URL(string: urlStr) else {
            statusText = "无效的视频流 URL"
            return
        }

        statusText = "正在连接 \(macIP)..."

        let config = URLSessionConfiguration.default
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.timeoutIntervalForRequest = 10
        session = URLSession(configuration: config, delegate: self, delegateQueue: nil)

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        dataTask = session?.dataTask(with: request)
        dataTask?.resume()

        DispatchQueue.main.async {
            self.fpsTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
                guard let self = self else { return }
                self.currentFPS = self.frameCountInSecond
                self.frameCountInSecond = 0
            }
        }
    }

    public func stopStream() {
        dataTask?.cancel()
        dataTask = nil
        session?.invalidateAndCancel()
        session = nil
        fpsTimer?.invalidate()
        fpsTimer = nil

        parser.reset()
        displayLayer?.flush()

        DispatchQueue.main.async {
            self.isConnected = false
            self.currentFPS = 0
            self.statusText = "已断开连接"
        }
    }

    // MARK: - URLSessionDataDelegate

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse, completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        if let http = response as? HTTPURLResponse {
            if http.statusCode == 200 {
                DispatchQueue.main.async {
                    self.isConnected = true
                    self.statusText = "已连接 · 串流正常"
                }
                completionHandler(.allow)
                return
            } else {
                DispatchQueue.main.async {
                    self.isConnected = false
                    self.statusText = "连接被拒绝 (HTTP \(http.statusCode))"
                }
                completionHandler(.cancel)
                return
            }
        }
        completionHandler(.allow)
    }

    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        processingQueue.async { [weak self] in
            guard let self = self else { return }
            let nalus = self.parser.append(data: data)

            for nalu in nalus {
                if let sampleBuffer = self.parser.createSampleBuffer(from: nalu) {
                    DispatchQueue.main.async {
                        if self.displayLayer?.status == .failed {
                            self.displayLayer?.flush()
                        }
                        self.displayLayer?.enqueue(sampleBuffer)
                        self.frameCountInSecond += 1
                        self.receivedFrames += 1
                    }
                }
            }
        }
    }

    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        DispatchQueue.main.async {
            self.isConnected = false
            if let err = error as NSError?, err.code != NSURLErrorCancelled {
                self.statusText = "网络中断: \(err.localizedDescription)"
            }
        }
    }
}
