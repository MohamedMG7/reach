/** The part of the WebSocket API the client uses, so tests can swap in a fake PC. */
export interface FrameSocket {
  onopen: (() => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onclose: (() => void) | null;
  onerror: (() => void) | null;
  send(data: Uint8Array): void;
  close(): void;
}

export type SocketFactory = (url: string) => FrameSocket;

export const createWebSocket: SocketFactory = (url) => {
  const socket = new WebSocket(url);
  socket.binaryType = 'arraybuffer';
  return socket as unknown as FrameSocket;
};
