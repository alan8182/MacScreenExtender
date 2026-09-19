import SwiftUI

public struct PairSheet: View {
    let macIP: String
    let onPaired: (String) -> Void
    let onCancel: () -> Void

    @State private var code: String = ""
    @State private var isSubmitting = false
    @State private var errorMessage: String?

    public init(macIP: String, onPaired: @escaping (String) -> Void, onCancel: @escaping () -> Void) {
        self.macIP = macIP
        self.onPaired = onPaired
        self.onCancel = onCancel
    }

    public var body: some View {
        NavigationView {
            VStack(spacing: 24) {
                Image(systemName: "lock.shield.fill")
                    .font(.system(size: 54))
                    .foregroundColor(.blue)
                    .padding(.top, 16)

                VStack(spacing: 8) {
                    Text("输入配对码")
                        .font(.title2.bold())
                    Text("请输入 Mac 顶部状态栏中显示的 4 位配对码")
                        .font(.subheadline)
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.center)
                }

                TextField("4 位数字", text: $code)
                    .font(.system(size: 32, weight: .bold, design: .monospaced))
                    .multilineTextAlignment(.center)
                    .keyboardType(.numberPad)
                    .frame(width: 200, height: 60)
                    .background(Color(uiColor: .secondarySystemBackground))
                    .cornerRadius(12)
                    .onChange(of: code) { newValue in
                        if newValue.count > 4 {
                            code = String(newValue.prefix(4))
                        }
                    }

                if let err = errorMessage {
                    Text(err)
                        .font(.footnote)
                        .foregroundColor(.red)
                }

                Spacer()

                Button(action: submitPairing) {
                    if isSubmitting {
                        ProgressView().tint(.white)
                    } else {
                        Text("确认配对")
                            .font(.headline)
                            .frame(maxWidth: .infinity)
                    }
                }
                .frame(maxWidth: .infinity, minHeight: 50)
                .background(code.count == 4 ? Color.blue : Color.gray.opacity(0.5))
                .foregroundColor(.white)
                .cornerRadius(12)
                .disabled(code.count != 4 || isSubmitting)
            }
            .padding(24)
            .navigationBarItems(leading: Button("取消", action: onCancel))
        }
        .frame(maxWidth: 420, maxHeight: 400)
    }

    private func submitPairing() {
        guard code.count == 4 else { return }
        isSubmitting = true
        errorMessage = nil

        let urlStr = "http://\(macIP):8080/pair?code=\(code)"
        guard let url = URL(string: urlStr) else { return }

        URLSession.shared.dataTask(with: url) { data, response, error in
            DispatchQueue.main.async {
                self.isSubmitting = false
                if let http = response as? HTTPURLResponse, http.statusCode == 200,
                   let data = data, let token = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) {
                    self.onPaired(token)
                } else {
                    self.errorMessage = "配对码错误或超时，请核对 Mac 菜单栏"
                }
            }
        }.resume()
    }
}
