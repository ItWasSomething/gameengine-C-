import { useEffect, useMemo, useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { formatBytes, upload } from '../api'
import { useAuth } from '../auth'
import { DropZone, Progress, TagInput, useTitle } from '../components/ui'

const MAX_SHOTS = 8
const IMAGE_TYPES = 'image/png,image/jpeg,image/gif,image/webp'

export function NewGame() {
  const { me, loading } = useAuth()
  const navigate = useNavigate()
  useTitle('Post a game')

  const [title, setTitle] = useState('')
  const [tagline, setTagline] = useState('')
  const [description, setDescription] = useState('')
  const [tags, setTags] = useState<string[]>([])
  const [cover, setCover] = useState<File | null>(null)
  const [shots, setShots] = useState<File[]>([])
  const [build, setBuild] = useState<File | null>(null)
  const [version, setVersion] = useState('1.0')
  const [changelog, setChangelog] = useState('')
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Object URLs for previews; revoked when the files change so they don't leak.
  const coverUrl = useMemo(() => cover && URL.createObjectURL(cover), [cover])
  const shotUrls = useMemo(() => shots.map(f => URL.createObjectURL(f)), [shots])
  useEffect(() => () => { if (coverUrl) URL.revokeObjectURL(coverUrl) }, [coverUrl])
  useEffect(() => () => shotUrls.forEach(u => URL.revokeObjectURL(u)), [shotUrls])

  if (loading) return <div className="loading-bar" />
  if (!me) return <Navigate to="/login?next=/new" replace />

  const addShots = (files: File[]) => {
    const images = files.filter(f => f.type.startsWith('image/'))
    setShots(s => [...s, ...images].slice(0, MAX_SHOTS))
  }
  const pickBuild = (files: File[]) => {
    const zip = files.find(f => f.name.toLowerCase().endsWith('.zip'))
    if (zip) { setBuild(zip); setError(null) } else if (files.length) setError('The build has to be a .zip file.')
  }

  const ready = title.trim().length > 0 && build != null
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!ready) return
    const form = new FormData()
    form.set('title', title)
    form.set('tagline', tagline)
    form.set('description', description)
    form.set('tags', tags.join(','))
    form.set('version', version)
    form.set('changelog', changelog)
    form.set('build', build!)
    if (cover) form.set('cover', cover)
    shots.forEach(s => form.append('screenshots', s))

    setError(null)
    setProgress(0)
    try {
      const res = await upload<{ slug: string }>('POST', '/api/games', form, setProgress)
      navigate(`/games/${res.slug}`)
    } catch (err) {
      setError((err as Error).message)
      setProgress(null)
    }
  }

  return (
    <div className="wrap">
      <form className="form-page" onSubmit={submit}>
        <span className="label">New game</span>
        <h1 style={{ marginTop: 12, marginBottom: 32 }}>Post a game.</h1>

        <section className="step">
          <div className="step-num">01<small>The basics</small></div>
          <div className="stack" style={{ gap: 20 }}>
            <label className="field"><span>Title *</span>
              <input className="input" value={title} maxLength={80} onChange={e => setTitle(e.target.value)} placeholder="Ember Run" required autoFocus /></label>
            <label className="field"><span>Tagline</span>
              <input className="input" value={tagline} maxLength={140} onChange={e => setTagline(e.target.value)} placeholder="One line that sells it" />
              <small>{tagline.length}/140 · shown on the game's card</small></label>
            <div className="field"><span>Tags</span><TagInput value={tags} onChange={setTags} /></div>
            <label className="field"><span>Description</span>
              <textarea className="textarea" value={description} maxLength={20000} onChange={e => setDescription(e.target.value)}
                placeholder={'What is it? How do you play?\n\nControls:\nA/D — move\nSpace — jump'} /></label>
          </div>
        </section>

        <section className="step">
          <div className="step-num">02<small>Images</small></div>
          <div className="stack" style={{ gap: 20 }}>
            <div className="field"><span>Cover image</span>
              {cover ? (
                <div className="shot" style={{ maxWidth: 420 }}>
                  <img src={coverUrl!} alt="" />
                  <button type="button" className="x" onClick={() => setCover(null)} aria-label="Remove cover">✕</button>
                </div>
              ) : <DropZone accept={IMAGE_TYPES} title="Drop a cover image" hint="16:9 works best · PNG, JPG, GIF, WebP · max 8 MB" onFiles={f => f[0] && setCover(f[0])} />}
              <small>Optional — without one, your first screenshot is used.</small>
            </div>
            <div className="field"><span>Screenshots ({shots.length}/{MAX_SHOTS})</span>
              {shots.length > 0 && (
                <div className="shots">
                  {shots.map((f, i) => (
                    <div key={shotUrls[i]} className="shot">
                      <img src={shotUrls[i]} alt="" />
                      <button type="button" className="x" onClick={() => setShots(s => s.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
                      <span className="badge">{f.name.length > 18 ? f.name.slice(0, 16) + '…' : f.name}</span>
                    </div>
                  ))}
                </div>
              )}
              {shots.length < MAX_SHOTS && <DropZone accept={IMAGE_TYPES} multiple title="Drop screenshots" hint="Select several at once" onFiles={addShots} />}
            </div>
          </div>
        </section>

        <section className="step">
          <div className="step-num">03<small>The build</small></div>
          <div className="stack" style={{ gap: 20 }}>
            <div className="field"><span>Game build (.zip) *</span>
              {build ? (
                <div className="file-chip">
                  <span className="name">{build.name}</span><span className="label">{formatBytes(build.size)}</span>
                  <span className="spacer" /><button type="button" className="link-btn" onClick={() => setBuild(null)}>change</button>
                </div>
              ) : <DropZone accept=".zip,application/zip" title="Drop your game's .zip" hint="In the editor: File → Build Game… makes this zip for you · max 1 GB" onFiles={pickBuild} />}
            </div>
            <div className="row" style={{ alignItems: 'flex-start', gap: 20 }}>
              <label className="field" style={{ width: 160 }}><span>Version</span>
                <input className="input" value={version} maxLength={32} onChange={e => setVersion(e.target.value)} /></label>
              <label className="field" style={{ flex: 1, minWidth: 240 }}><span>Release notes</span>
                <input className="input" value={changelog} onChange={e => setChangelog(e.target.value)} placeholder="First release!" /></label>
            </div>
          </div>
        </section>

        {error && <div className="error-box" style={{ marginBottom: 20 }}>{error}</div>}
        {progress != null && (
          <div className="stack" style={{ gap: 8, marginBottom: 20 }}>
            <Progress value={progress} />
            <span className="label">{progress < 1 ? `Uploading… ${Math.round(progress * 100)}%` : 'Processing…'}</span>
          </div>
        )}
        <div className="submit-bar">
          {!ready && <span className="muted">{!title.trim() ? 'Add a title' : 'Attach a .zip build'} to publish.</span>}
          <button className="btn primary big" disabled={!ready || progress != null}>Publish game →</button>
        </div>
      </form>
    </div>
  )
}
