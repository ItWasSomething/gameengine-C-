import { useCallback, useEffect, useState } from 'react'
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom'
import { api, formatBytes, timeAgo, upload, type GameDetail, type Screenshot, type Version } from '../api'
import { useAuth } from '../auth'
import { DropZone, Empty, Progress, TagInput, useTitle, useToast } from '../components/ui'

const IMAGE_TYPES = 'image/png,image/jpeg,image/gif,image/webp'

// Edit page: each section saves on its own, so you can e.g. upload a new
// version without touching the description.
export function EditGame() {
  const { slug } = useParams()
  const { me, loading } = useAuth()
  const navigate = useNavigate()
  const [game, setGame] = useState<GameDetail | null>(null)
  const [missing, setMissing] = useState(false)
  const [toast, showToast] = useToast()
  useTitle(game ? `Edit ${game.title}` : 'Edit')

  const load = useCallback(() => {
    api<GameDetail>(`/api/games/${slug}`).then(setGame).catch(() => setMissing(true))
  }, [slug])
  useEffect(load, [load])

  if (loading) return <div className="loading-bar" />
  if (!me) return <Navigate to={`/login?next=/games/${slug}/edit`} replace />
  if (missing) return <div className="wrap"><Empty title="Game not found" /></div>
  if (!game) return <div className="loading-bar" />
  if (!game.isOwner) return <div className="wrap"><Empty title="This isn't your game"><Link className="btn" to={`/games/${slug}`}>View game</Link></Empty></div>

  const remove = async () => {
    if (prompt(`This deletes "${game.title}", every version, screenshot, like and comment. Type the title to confirm:`) !== game.title) return
    await api(`/api/games/${game.slug}`, { method: 'DELETE' })
    navigate(`/u/${me.username}`)
  }

  return (
    <div className="wrap">
      <div className="form-page">
        <Link to={`/games/${game.slug}`} className="label">← Back to game page</Link>
        <h1 style={{ marginTop: 12, marginBottom: 32 }}>Edit {game.title}</h1>
        <Basics game={game} onSaved={() => { showToast('Saved'); load() }} />
        <Images game={game} onChanged={load} />
        <Versions game={game} onChanged={() => { showToast('New version published'); load() }} />
        <section className="step">
          <div className="step-num">04<small>Danger zone</small></div>
          <div className="stack">
            <p className="muted">Deleting a game can't be undone.</p>
            <div><button className="btn" onClick={remove}>Delete this game</button></div>
          </div>
        </section>
      </div>
      {toast}
    </div>
  )
}

function Basics({ game, onSaved }: { game: GameDetail; onSaved: () => void }) {
  const [title, setTitle] = useState(game.title)
  const [tagline, setTagline] = useState(game.tagline)
  const [description, setDescription] = useState(game.description)
  const [tags, setTags] = useState(game.tags)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const dirty = title !== game.title || tagline !== game.tagline || description !== game.description || tags.join() !== game.tags.join()

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api(`/api/games/${game.slug}`, { method: 'PATCH', json: { title, tagline, description, tags } })
      onSaved()
    } catch (err) { setError((err as Error).message) } finally { setBusy(false) }
  }

  return (
    <form className="step" onSubmit={save}>
      <div className="step-num">01<small>Details</small></div>
      <div className="stack" style={{ gap: 20 }}>
        <label className="field"><span>Title</span><input className="input" value={title} maxLength={80} onChange={e => setTitle(e.target.value)} required /></label>
        <label className="field"><span>Tagline</span><input className="input" value={tagline} maxLength={140} onChange={e => setTagline(e.target.value)} /></label>
        <div className="field"><span>Tags</span><TagInput value={tags} onChange={setTags} /></div>
        <label className="field"><span>Description</span><textarea className="textarea" value={description} maxLength={20000} onChange={e => setDescription(e.target.value)} /></label>
        {error && <div className="error-box">{error}</div>}
        <div><button className="btn primary" disabled={!dirty || busy}>{busy ? 'Saving…' : 'Save details'}</button></div>
      </div>
    </form>
  )
}

function Images({ game, onChanged }: { game: GameDetail; onChanged: () => void }) {
  const [shots, setShots] = useState<Screenshot[]>(game.screenshots)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setShots(game.screenshots) }, [game.screenshots])

  const run = async (work: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try { await work(); onChanged() } catch (err) { setError((err as Error).message) } finally { setBusy(false) }
  }

  const setCover = (files: File[]) => files[0] && run(() => {
    const form = new FormData()
    form.set('cover', files[0])
    return upload('PUT', `/api/games/${game.slug}/cover`, form)
  })
  const addShots = (files: File[]) => files.length && run(() => {
    const form = new FormData()
    files.forEach(f => form.append('screenshots', f))
    return upload('POST', `/api/games/${game.slug}/screenshots`, form)
  })
  const removeShot = (id: number) => run(() => api(`/api/games/${game.slug}/screenshots/${id}`, { method: 'DELETE' }))
  const move = (i: number, d: number) => {
    const next = [...shots]
    const [s] = next.splice(i, 1)
    next.splice(i + d, 0, s)
    setShots(next)
    run(() => api(`/api/games/${game.slug}/screenshots/order`, { method: 'PUT', json: { ids: next.map(x => x.id) } }))
  }

  return (
    <section className="step">
      <div className="step-num">02<small>Images</small></div>
      <div className="stack" style={{ gap: 20 }}>
        <div className="field"><span>Cover</span>
          <div className="row" style={{ alignItems: 'stretch' }}>
            {game.coverUrl && <div className="shot" style={{ width: 260 }}><img src={game.coverUrl} alt="" /></div>}
            <div style={{ flex: 1, minWidth: 220 }}><DropZone accept={IMAGE_TYPES} title="Replace cover" hint="Drop or click" onFiles={setCover} /></div>
          </div>
        </div>
        <div className="field"><span>Screenshots ({shots.length}/8)</span>
          {shots.length > 0 && (
            <div className="shots">
              {shots.map((s, i) => (
                <div key={s.id} className="shot">
                  <img src={s.url} alt="" />
                  <div className="move">
                    {i > 0 && <button type="button" onClick={() => move(i, -1)} aria-label="Move left" disabled={busy}>←</button>}
                    {i < shots.length - 1 && <button type="button" onClick={() => move(i, 1)} aria-label="Move right" disabled={busy}>→</button>}
                  </div>
                  <button type="button" className="x" onClick={() => removeShot(s.id)} aria-label="Delete" disabled={busy}>✕</button>
                  {s.url === game.coverUrl && <span className="badge">cover</span>}
                </div>
              ))}
            </div>
          )}
          {shots.length < 8 && <DropZone accept={IMAGE_TYPES} multiple title="Add screenshots" hint="Drop or click · several at once" onFiles={addShots} />}
        </div>
        {busy && <div className="loading-bar" />}
        {error && <div className="error-box">{error}</div>}
      </div>
    </section>
  )
}

function Versions({ game, onChanged }: { game: GameDetail; onChanged: () => void }) {
  const [build, setBuild] = useState<File | null>(null)
  const [version, setVersion] = useState(() => suggestNext(game.versions[0]?.version))
  const [changelog, setChangelog] = useState('')
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const publish = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!build) return
    const form = new FormData()
    form.set('build', build)
    form.set('version', version)
    form.set('changelog', changelog)
    setError(null)
    setProgress(0)
    try {
      await upload('POST', `/api/games/${game.slug}/versions`, form, setProgress)
      setBuild(null)
      setChangelog('')
      onChanged()
    } catch (err) { setError((err as Error).message) } finally { setProgress(null) }
  }

  const removeVersion = async (v: Version) => {
    if (!confirm(`Delete version ${v.version}? Its download goes away.`)) return
    try { await api(`/api/games/${game.slug}/versions/${v.id}`, { method: 'DELETE' }); onChanged() }
    catch (err) { setError((err as Error).message) }
  }

  return (
    <form className="step" onSubmit={publish}>
      <div className="step-num">03<small>Versions</small></div>
      <div className="stack" style={{ gap: 20 }}>
        <div className="field"><span>Upload a new version</span>
          {build ? (
            <div className="file-chip"><span className="name">{build.name}</span><span className="label">{formatBytes(build.size)}</span>
              <span className="spacer" /><button type="button" className="link-btn" onClick={() => setBuild(null)}>change</button></div>
          ) : <DropZone accept=".zip,application/zip" title="Drop the new .zip" hint="Players get this from the Download button" onFiles={f => setBuild(f.find(x => x.name.toLowerCase().endsWith('.zip')) ?? null)} />}
        </div>
        <div className="row" style={{ alignItems: 'flex-start', gap: 20 }}>
          <label className="field" style={{ width: 160 }}><span>Version</span><input className="input" value={version} onChange={e => setVersion(e.target.value)} /></label>
          <label className="field" style={{ flex: 1, minWidth: 240 }}><span>What changed?</span><textarea className="textarea" style={{ minHeight: 48 }} value={changelog} onChange={e => setChangelog(e.target.value)} placeholder="Fixed the double-jump bug…" /></label>
        </div>
        {progress != null && <Progress value={progress} />}
        {error && <div className="error-box">{error}</div>}
        <div><button className="btn primary" disabled={!build || progress != null}>Publish version</button></div>

        <div className="field"><span>Published versions</span>
          <ul className="versions" style={{ margin: 0, padding: 0 }}>
            {game.versions.map(v => (
              <li key={v.id}>
                <span className="v">v{v.version}</span>
                {game.versions.length > 1 ? <button type="button" className="link-btn" onClick={() => removeVersion(v)}>delete</button> : <span />}
                <span className="label" style={{ gridColumn: '1 / -1' }}>{timeAgo(v.createdAt)} · {formatBytes(v.sizeBytes)} · {v.downloads} downloads</span>
                {v.changelog && <span className="changelog">{v.changelog}</span>}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </form>
  )
}

// "1.2" → "1.3", "0.9.4" → "0.9.5"; anything else is left for the user to type.
function suggestNext(v?: string): string {
  if (!v) return '1.0'
  const m = v.match(/^(.*?)(\d+)$/)
  return m ? m[1] + (Number(m[2]) + 1) : v
}
