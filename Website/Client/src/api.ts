// Everything the site knows about the server: the JSON shapes (mirroring
// Server/Api/Dtos.cs) and one small fetch wrapper.

export interface UserSummary { username: string; displayName: string; avatarUrl: string }
export interface Me extends UserSummary { bio: string; hasGitHub: boolean }

export interface GameCard {
  slug: string; title: string; tagline: string; coverUrl: string | null; author: UserSummary
  tags: string[]; likes: number; downloads: number; latestVersion: string | null; updatedAt: string
}

export interface Screenshot { id: number; url: string }
export interface Version { id: number; version: string; changelog: string; sizeBytes: number; downloads: number; createdAt: string }

export interface GameDetail {
  slug: string; title: string; tagline: string; description: string; coverUrl: string | null; author: UserSummary
  tags: string[]; likes: number; downloads: number; likedByMe: boolean; isOwner: boolean; commentCount: number
  screenshots: Screenshot[]; versions: Version[]; createdAt: string; updatedAt: string
}

export interface Comment { id: number; author: UserSummary; body: string; createdAt: string; canDelete: boolean }
export interface PagedGames { items: GameCard[]; page: number; pageSize: number; total: number }
export interface Profile { user: UserSummary; bio: string; joinedAt: string; games: GameCard[]; totalDownloads: number; totalLikes: number }
export interface TagCount { name: string; count: number }
export interface Token { id: number; name: string; createdAt: string; lastUsedAt: string | null }
export interface Providers { github: boolean; dev: boolean }

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

// fetch() + JSON + errors. Every write sends "X-MyEngine: 1" — the server
// rejects cookie-authenticated writes without it (that's the CSRF guard, see
// Server/Auth/AuthSetup.cs).
export async function api<T = unknown>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('X-MyEngine', '1')
  let body = init.body
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json')
    body = JSON.stringify(init.json)
  }
  const res = await fetch(path, { ...init, headers, body, credentials: 'same-origin' })
  if (!res.ok) {
    let message = res.status === 429 ? 'Slow down — too many requests. Try again in a minute.' : `Request failed (${res.status}).`
    try {
      const data = await res.json()
      message = data.error ?? data.detail ?? data.title ?? message
    } catch { /* not JSON */ }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

// fetch() can't report upload progress; XMLHttpRequest can. Builds can be
// hundreds of MB, so a progress bar matters.
export function upload<T>(method: string, path: string, form: FormData, onProgress?: (fraction: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open(method, path)
    xhr.setRequestHeader('X-MyEngine', '1')
    xhr.upload.onprogress = e => { if (e.lengthComputable) onProgress?.(e.loaded / e.total) }
    xhr.onload = () => {
      let data: { error?: string } | undefined
      try { data = xhr.responseText ? JSON.parse(xhr.responseText) : undefined } catch { /* not JSON */ }
      if (xhr.status >= 200 && xhr.status < 300) resolve(data as T)
      else reject(new ApiError(xhr.status, data?.error ?? `Upload failed (${xhr.status}).`))
    }
    xhr.onerror = () => reject(new ApiError(0, 'Network error — is the server running?'))
    xhr.send(form)
  })
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(0)} KB`
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`
  return `${(n / 1024 ** 3).toFixed(2)} GB`
}

export function formatCount(n: number): string {
  if (n < 1000) return String(n)
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 10_000 ? 1 : 0)}k`
  return `${(n / 1_000_000).toFixed(1)}M`
}

export function timeAgo(iso: string): string {
  const seconds = (Date.now() - new Date(iso).getTime()) / 1000
  const units: [number, string][] = [[31536000, 'y'], [2592000, 'mo'], [604800, 'w'], [86400, 'd'], [3600, 'h'], [60, 'm']]
  for (const [size, label] of units) if (seconds >= size) return `${Math.floor(seconds / size)}${label} ago`
  return 'just now'
}
