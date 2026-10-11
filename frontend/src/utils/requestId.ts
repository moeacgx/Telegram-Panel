/** 请求幂等编号：普通 HTTP 下也使用浏览器提供的安全随机数。 */
export function createRequestId(cryptoProvider: Crypto = globalThis.crypto): string {
  if (typeof cryptoProvider?.randomUUID === 'function') return cryptoProvider.randomUUID()

  if (typeof cryptoProvider?.getRandomValues !== 'function') {
    throw new Error('浏览器不支持安全随机数，请使用现代浏览器后重试')
  }
  const bytes = cryptoProvider.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}
