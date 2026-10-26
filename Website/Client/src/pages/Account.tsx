import { useEffect, useState } from 'react'
import { Link, Navigate, useParams, useSearchParams } from 'react-router-dom'
import { api, formatCount, timeAgo, type Me, type Profile, type Token } from '../api'
import { githubLoginUrl, useAuth } from '../auth'
import { Avatar, Empty, GameCardView, GridSkeleton, useTitle, useToast } from '../components/ui'

// ---- /u/:username -------------------------------------------------------------

export function ProfilePage() {
  const { username } = useParams()
  const { me } = useAuth()
  const [profile, setProfile] = useState<Profile | null>(null)
  const [missing, setMissing] = useState(false)
  useTitle(profile?.user.displayName ?? '')

  useEffect(() => {
    setProfile(null)
    api<Profile>(`/api/users/${username}`).then(setProfile).catch(() => setMissing(true))
  }, [username])

  if (missing) return <div className="wrap"><Empty title="No such user" /></div>
  const isMe = me?.username === username

  return (
    <div className="wrap">
      {!profile ? <><div className="profile-head"><div className="avatar big skeleton" /></div><GridSkeleton count={3} /></> : <>
        <div className="profile-head">
          <Avatar user={profile.user} size="big" />
          <div className="stack" style={{ gap: 8, flex: 1, minWidth: 240 }}>
            <span className="label">@{profile.user.username} · joined {new Date(profile.joinedAt).toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}</span>
            <h1 style={{ fontSize: 'clamp(32px, 5vw, 56px)' }}>{profile.user.displayName}</h1>
            {profile.bio && <p className="muted" style={{ maxWidth: '60ch' }}>{profile.bio}</p>}
            {isMe && <div className="row" style={{ marginTop: 8 }}><Link className="btn small" to="/settings">Edit profile</Link><Link className="btn small primary" to="/new">Post a game</Link></div>}
          </div>
          <div className="stats">
            <div><span className="label">Games</span><b>{profile.games.length}</b></div>
            <div><span className="label">Downloads</span><b>{formatCount(profile.totalDownloads)}</b></div>
            <div><span className="label">Likes</span><b>{formatCount(profile.totalLikes)}</b></div>
          </div>
        </div>
        {profile.games.length === 0
          ? <Empty title={isMe ? "You haven't posted a game yet" : 'No games yet'}>{isMe && <Link className="btn primary" to="/new">Post your first game</Link>}</Empty>
          : <div className="grid">{profile.games.map(g => <GameCardView key={g.slug} game={g} />)}</div>}
      </>}
    </div>
  )
}

// ---- /settings ------------------------------------------------------------------

export function SettingsPage() {
  const { me, loading, setMe } = useAuth()
  const [displayName, setDisplayName] = useState('')
  const [bio, setBio] = useState('')
  const [tokens, setTokens] = useState<Token[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [toast, showToast] = useToast()
  useTitle('Settings')

  useEffect(() => {
    if (!me) return
    setDisplayName(me.displayName)
    setBio(me.bio)
    api<Token[]>('/api/me/tokens').then(setTokens).catch(() => setTokens([]))
  }, [me])

  if (loading) return <div className="loading-bar" />
  if (!me) return <Navigate to="/login?next=/settings" replace />

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    try { setMe(await api<Me>('/api/me', { method: 'PATCH', json: { displayName, bio } })); showToast('Profile saved') }
    catch (err) { setError((err as Error).message) }
  }
  const revoke = async (id: number) => {
    if (!confirm('Sign this editor out? It will have to sign in again to publish.')) return
    await api(`/api/me/tokens/${id}`, { method: 'DELETE' })
    setTokens(t => t?.filter(x => x.id !== id) ?? null)
  }

  return (
    <div className="wrap">
      <div className="form-page">
        <span className="label">Account</span>
        <h1 style={{ marginTop: 12, marginBottom: 32 }}>Settings</h1>
        <form className="step" onSubmit={save}>
          <div className="step-num">01<small>Profile</small></div>
          <div className="stack" style={{ gap: 20 }}>
            <label className="field"><span>Display name</span><input className="input" value={displayName} maxLength={50} onChange={e => setDisplayName(e.target.value)} /></label>
            <label className="field"><span>Bio</span><textarea className="textarea" style={{ minHeight: 96 }} value={bio} maxLength={500} onChange={e => setBio(e.target.value)} placeholder="What do you make?" />
              <small>{bio.length}/500</small></label>
            {error && <div className="error-box">{error}</div>}
            <div className="row"><button className="btn primary">Save profile</button><Link className="link-btn" to={`/u/${me.username}`}>View public profile</Link></div>
          </div>
        </form>
        <section className="step">
          <div className="step-num">02<small>Signed-in editors</small></div>
          <div className="stack">
            <p className="muted">Each MyEngine editor you've signed in with (File → Publish to MyEngine Games) appears here. Revoke one you don't use anymore.</p>
            {tokens == null ? <div className="loading-bar" /> : tokens.length === 0 ? <p className="notice">No editors signed in.</p> : (
              <table className="tokens">
                <thead><tr><th>Name</th><th>Signed in</th><th>Last used</th><th /></tr></thead>
                <tbody>{tokens.map(t => (
                  <tr key={t.id}><td>{t.name}</td><td>{timeAgo(t.createdAt)}</td><td>{t.lastUsedAt ? timeAgo(t.lastUsedAt) : 'never'}</td>
                    <td style={{ textAlign: 'right' }}><button className="btn small" onClick={() => revoke(t.id)}>Revoke</button></td></tr>
                ))}</tbody>
              </table>
            )}
          </div>
        </section>
      </div>
      {toast}
    </div>
  )
}

// ---- /login ---------------------------------------------------------------------

export function LoginPage() {
  const { me, providers, refresh } = useAuth()
  const [params] = useSearchParams()
  const next = safeNext(params.get('next'))
  const [devName, setDevName] = useState('')
  const [error, setError] = useState<string | null>(null)
  useTitle('Sign in')

  if (me) return <Navigate to={next} replace />

  const devLogin = async (e: React.FormEvent) => {
    e.preventDefault()
    try { await api('/auth/dev-login', { method: 'POST', json: { username: devName } }); await refresh() }
    catch (err) { setError((err as Error).message) }
  }

  return (
    <div className="center-page">
      <div className="center-card">
        <span className="label">Sign in</span>
        <h2>Welcome to MyEngine Games.</h2>
        <p className="muted">Sign in to post games, like, and comment. Browsing and downloading don't need an account.</p>
        {providers.github ? (
          <a className="btn primary big block" href={githubLoginUrl(next)}>
            <svg width="20" height="20" viewBox="0 0 16 16" fill="currentColor" aria-hidden><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z" /></svg>
            Continue with GitHub
          </a>
        ) : (
          <div className="notice">GitHub sign-in isn't set up on this server yet. See <span className="mono">Website/README.md</span>.</div>
        )}
        {providers.dev && (
          <form className="stack" style={{ gap: 12 }} onSubmit={devLogin}>
            <div className="divider label">dev only</div>
            <input className="input" value={devName} onChange={e => setDevName(e.target.value)} placeholder="Any username" />
            <button className="btn block" disabled={!devName.trim()}>Dev sign-in (no password)</button>
            {error && <div className="error-box">{error}</div>}
          </form>
        )}
      </div>
    </div>
  )
}

// ---- /device?code=XXXX-XXXX : approve the editor --------------------------------------

export function DevicePage() {
  const { me, loading } = useAuth()
  const [params] = useSearchParams()
  const [code, setCode] = useState((params.get('code') ?? '').toUpperCase())
  const [state, setState] = useState<'checking' | 'ready' | 'invalid' | 'approved' | 'denied'>('checking')
  useTitle('Connect the editor')

  useEffect(() => {
    if (!me) return
    if (!code) { setState('invalid'); return }
    setState('checking')
    api(`/api/device/${encodeURIComponent(code)}`).then(() => setState('ready')).catch(() => setState('invalid'))
  }, [me, code])

  if (loading) return <div className="loading-bar" />
  if (!me) return <Navigate to={`/login?next=${encodeURIComponent(`/device?code=${code}`)}`} replace />

  const answer = async (approve: boolean) => {
    try {
      await api('/api/device/approve', { method: 'POST', json: { userCode: code, approve } })
      setState(approve ? 'approved' : 'denied')
    } catch { setState('invalid') }
  }

  return (
    <div className="center-page">
      <div className="center-card">
        <span className="label">Connect the MyEngine editor</span>
        {state === 'approved' ? <>
          <h2>Done — go back to the editor.</h2>
          <p className="muted">It's signed in as <b>{me.displayName}</b> and can publish games to your account. You can sign it out anytime in <Link className="link-btn" to="/settings">Settings</Link>.</p>
        </> : state === 'denied' ? <>
          <h2>Request denied.</h2><p className="muted">The editor was not signed in. You can close this tab.</p>
        </> : state === 'invalid' ? <>
          <h2>Code not found.</h2>
          <p className="muted">It may have expired (codes last 10 minutes). Type the code shown in the editor, or start again from File → Publish.</p>
          <input className="input mono" value={code} onChange={e => setCode(e.target.value.toUpperCase())} placeholder="XXXX-XXXX" style={{ textAlign: 'center', fontSize: 22, letterSpacing: '0.1em' }} />
        </> : <>
          <h2>Sign the editor in as {me.displayName}?</h2>
          <p className="muted">Check that this code matches the one in your editor window:</p>
          <div className="code-display">{code}</div>
          <p className="muted" style={{ fontSize: 13 }}>Only approve if you just clicked Sign in inside MyEngine yourself. The editor will be able to post and update games on your account.</p>
          <div className="row">
            <button className="btn primary" style={{ flex: 1 }} disabled={state !== 'ready'} onClick={() => answer(true)}>Approve</button>
            <button className="btn" style={{ flex: 1 }} disabled={state !== 'ready'} onClick={() => answer(false)}>Deny</button>
          </div>
        </>}
      </div>
    </div>
  )
}

export function NotFound() {
  useTitle('Not found')
  return <div className="wrap"><Empty title="404 — nothing here"><Link className="btn" to="/">Browse games</Link></Empty></div>
}

function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') ? next : '/'
}

