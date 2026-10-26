import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, ApiError, type Me, type Providers } from './api'

// Who's signed in, shared with every page. The cookie itself is HttpOnly
// (JavaScript can't read it), so we ask the server: GET /api/me → 200 or 401.

interface AuthState {
  me: Me | null
  loading: boolean
  providers: Providers
  refresh: () => Promise<void>
  logout: () => Promise<void>
  setMe: (me: Me) => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(true)
  const [providers, setProviders] = useState<Providers>({ github: false, dev: false })

  const refresh = useCallback(async () => {
    try {
      setMe(await api<Me>('/api/me'))
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) setMe(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    refresh()
    api<Providers>('/api/auth/providers').then(setProviders).catch(() => {})
  }, [refresh])

  const logout = useCallback(async () => {
    await api('/auth/logout', { method: 'POST' })
    setMe(null)
  }, [])

  return <AuthContext.Provider value={{ me, loading, providers, refresh, logout, setMe }}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}

// Full-page navigation (not fetch): GitHub's login page has to load in the tab.
export function githubLoginUrl(returnTo: string = window.location.pathname + window.location.search) {
  return `/auth/github?returnUrl=${encodeURIComponent(returnTo)}`
}
