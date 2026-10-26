import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, formatBytes, formatCount, timeAgo, type Comment, type GameDetail } from '../api'
import { useAuth } from '../auth'
import { Avatar, Empty, Lightbox, useTitle, useToast } from '../components/ui'

export function GamePage() {
  const { slug } = useParams()
  const { me } = useAuth()
  const navigate = useNavigate()
  const [game, setGame] = useState<GameDetail | null>(null)
  const [missing, setMissing] = useState(false)
  const [shot, setShot] = useState(0)
  const [lightbox, setLightbox] = useState(false)
  const [toast, showToast] = useToast()
  useTitle(game?.title ?? '')

  const load = useCallback(() => {
    api<GameDetail>(`/api/games/${slug}`).then(setGame).catch(() => setMissing(true))
  }, [slug])
  useEffect(load, [load])

  if (missing) return <div className="wrap"><Empty title="Game not found"><Link className="btn" to="/">Back to browse</Link></Empty></div>
  if (!game) return <div className="loading-bar" />

  // The cover is shown first unless it's already one of the screenshots.
  const images = [...(game.coverUrl && !game.screenshots.some(s => s.url === game.coverUrl) ? [game.coverUrl] : []), ...game.screenshots.map(s => s.url)]
  const latest = game.versions[0]

  const toggleLike = async () => {
    if (!me) { navigate(`/login?next=/games/${game.slug}`); return }
    const res = await api<{ liked: boolean; likes: number }>(`/api/games/${game.slug}/like`, { method: game.likedByMe ? 'DELETE' : 'POST' })
    setGame({ ...game, likedByMe: res.liked, likes: res.likes })
  }

  // A plain link would work too; this also bumps the counter on screen
  // without refetching the whole game.
  const download = (versionId?: number) => {
    window.location.href = `/api/games/${game.slug}/download${versionId ? `/${versionId}` : ''}`
    setGame(g => g && { ...g, downloads: g.downloads + 1 })
    showToast('Download started')
  }

  return (
    <>
      <section className="game-head">
        <div className="wrap">
          <div className="row" style={{ marginTop: 0, marginBottom: 16 }}>
            {game.tags.map(t => <Link key={t} className="tag" to={`/?tag=${t}`}>{t}</Link>)}
          </div>
          <h1>{game.title}</h1>
          {game.tagline && <p className="tagline">{game.tagline}</p>}
          <div className="row">
            <Link to={`/u/${game.author.username}`} className="row" style={{ gap: 10 }}>
              <Avatar user={game.author} />
              <b>{game.author.displayName}</b>
            </Link>
            <span className="label">Updated {timeAgo(game.updatedAt)}</span>
            <span className="spacer" />
            {game.isOwner && <Link className="btn small" to={`/games/${game.slug}/edit`}>Edit game</Link>}
          </div>
        </div>
      </section>

      <div className="wrap game-layout">
        <div className="game-media">
          {images.length > 0 ? (
            <div className="viewer">
              <div className="viewer-main" onClick={() => setLightbox(true)} title="Click to enlarge">
                <img src={images[shot]} alt={`${game.title} screenshot ${shot + 1}`} />
              </div>
              {images.length > 1 && (
                <div className="thumbs">
                  {images.map((src, i) => (
                    <button key={src} className={i === shot ? 'on' : ''} onClick={() => setShot(i)} aria-label={`Screenshot ${i + 1}`}>
                      <img src={src} alt="" />
                    </button>
                  ))}
                </div>
              )}
            </div>
          ) : <div className="viewer"><div className="viewer-main"><div className="cover-empty" style={{ background: 'var(--paper)' }}><b>NO SCREENSHOTS</b></div></div></div>}
        </div>

        <div className="game-details">
          <div className="section">
            <span className="label">About this game</span>
            {game.description ? <div className="prose">{game.description}</div> : <p className="muted">No description yet.</p>}
          </div>

          <Comments slug={game.slug} count={game.commentCount} />
        </div>

        <aside className="game-aside">
          <div className="panel download-panel">
            <div className="panel-pad stack" style={{ gap: 12 }}>
              <span className="label">Windows · .zip{latest ? ` · ${formatBytes(latest.sizeBytes)}` : ''}</span>
              <button className="btn primary big block" disabled={!latest} onClick={() => download()}>
                ↓ Download{latest ? ` v${latest.version}` : ''}
              </button>
              <button className={`btn block${game.likedByMe ? ' on' : ''}`} onClick={toggleLike} aria-pressed={game.likedByMe}>
                {game.likedByMe ? '♥ Liked' : '♡ Like'}
              </button>
            </div>
            <div className="meta">
              <div><span className="label">Downloads</span><b>{formatCount(game.downloads)}</b></div>
              <div><span className="label">Likes</span><b>{formatCount(game.likes)}</b></div>
              <div><span className="label">Version</span><b>{latest?.version ?? '—'}</b></div>
              <div><span className="label">Released</span><b style={{ fontSize: 14 }}>{new Date(game.createdAt).toLocaleDateString()}</b></div>
            </div>
          </div>

          <div className="panel panel-pad">
            <span className="label">Versions</span>
            <ul className="versions" style={{ margin: '8px 0 0', padding: 0 }}>
              {game.versions.map((v, i) => (
                <li key={v.id}>
                  <span className="v">v{v.version}{i === 0 && <span className="label" style={{ marginLeft: 8 }}>latest</span>}</span>
                  <button className="link-btn" onClick={() => download(v.id)}>{formatBytes(v.sizeBytes)} ↓</button>
                  <span className="label" style={{ gridColumn: '1 / -1' }}>{timeAgo(v.createdAt)} · {formatCount(v.downloads)} downloads</span>
                  {v.changelog && <span className="changelog">{v.changelog}</span>}
                </li>
              ))}
            </ul>
          </div>
          <p className="muted" style={{ fontSize: 13, marginTop: 16 }}>
            Unzip and run the .exe. Windows may warn about an unknown publisher — games here aren't code-signed.
          </p>
        </aside>
      </div>

      {lightbox && <Lightbox images={images} index={shot} onIndex={setShot} onClose={() => setLightbox(false)} />}
      {toast}
    </>
  )
}

function Comments({ slug, count }: { slug: string; count: number }) {
  const { me } = useAuth()
  const [comments, setComments] = useState<Comment[] | null>(null)
  const [body, setBody] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { api<Comment[]>(`/api/games/${slug}/comments`).then(setComments).catch(() => setComments([])) }, [slug, me])

  const post = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!body.trim()) return
    setBusy(true)
    setError(null)
    try {
      const c = await api<Comment>(`/api/games/${slug}/comments`, { method: 'POST', json: { body } })
      setComments(cs => [c, ...(cs ?? [])])
      setBody('')
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const remove = async (id: number) => {
    if (!confirm('Delete this comment?')) return
    await api(`/api/comments/${id}`, { method: 'DELETE' })
    setComments(cs => cs?.filter(c => c.id !== id) ?? null)
  }

  const total = comments?.length ?? count
  return (
    <div className="section">
      <span className="label">Comments ({total})</span>
      {me ? (
        <form className="stack" style={{ gap: 12, marginBottom: 8 }} onSubmit={post}>
          <textarea className="textarea" style={{ minHeight: 96 }} value={body} maxLength={2000}
            onChange={e => setBody(e.target.value)} placeholder="Say something about this game…"
            onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) post(e) }} />
          {error && <div className="error-box">{error}</div>}
          <div className="row"><span className="label">Ctrl+Enter to post</span><span className="spacer" />
            <button className="btn primary" disabled={busy || !body.trim()}>{busy ? 'Posting…' : 'Post comment'}</button></div>
        </form>
      ) : (
        <div className="notice row"><span>Sign in to join the conversation.</span><span className="spacer" />
          <Link className="btn small primary" to={`/login?next=/games/${slug}`}>Sign in</Link></div>
      )}
      <div className="comments">
        {comments?.length === 0 && <p className="muted" style={{ padding: '18px 0' }}>No comments yet.</p>}
        {comments?.map(c => (
          <div key={c.id} className="comment">
            <Avatar user={c.author} />
            <div>
              <div className="comment-head">
                <Link to={`/u/${c.author.username}`}>{c.author.displayName}</Link>
                <span className="label">{timeAgo(c.createdAt)}</span>
                <span className="spacer" />
                {c.canDelete && <button className="link-btn" onClick={() => remove(c.id)}>delete</button>}
              </div>
              <p>{c.body}</p>
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
