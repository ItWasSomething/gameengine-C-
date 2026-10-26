import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, type PagedGames, type TagCount } from '../api'
import { Empty, GameCardView, GridSkeleton, useTitle } from '../components/ui'

const SORTS = [
  ['new', 'Newest'],
  ['popular', 'Popular'],
  ['downloads', 'Most downloaded'],
  ['updated', 'Updated'],
] as const

// Browse page. Search, sort, tag and page all live in the URL (?q=&tag=&sort=),
// so a filtered view can be bookmarked or shared and Back works.
export function Home() {
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''
  const tag = params.get('tag') ?? ''
  const sort = params.get('sort') ?? 'new'
  const page = Number(params.get('page') ?? 1)

  const [draft, setDraft] = useState(q)
  const [data, setData] = useState<PagedGames | null>(null)
  const [tags, setTags] = useState<TagCount[]>([])
  const [error, setError] = useState<string | null>(null)
  useTitle('')

  useEffect(() => { setDraft(q) }, [q])
  useEffect(() => { api<TagCount[]>('/api/tags').then(setTags).catch(() => {}) }, [])
  useEffect(() => {
    setData(null)
    setError(null)
    const search = new URLSearchParams({ sort, page: String(page) })
    if (q) search.set('q', q)
    if (tag) search.set('tag', tag)
    api<PagedGames>(`/api/games?${search}`).then(setData).catch(e => setError(e.message))
  }, [q, tag, sort, page])

  const update = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) v ? next.set(k, v) : next.delete(k)
    if (!('page' in changes)) next.delete('page')
    setParams(next)
  }

  const pages = data ? Math.ceil(data.total / data.pageSize) : 0
  const filtered = q || tag

  return (
    <>
      <section className="hero">
        <div className="wrap">
          <h1>Games made with MyEngine.</h1>
          <p>Download free games built on a from-scratch C# engine — or post your own with screenshots, a description and a build.</p>
          <form className="search" style={{ marginTop: 32, maxWidth: 720 }} onSubmit={e => { e.preventDefault(); update({ q: draft.trim() || null }) }}>
            <input value={draft} onChange={e => setDraft(e.target.value)} placeholder="Search games, tags…" aria-label="Search games" />
            <button type="submit">Search</button>
          </form>
        </div>
      </section>

      <div className="wrap">
        <div className="toolbar">
          <div className="tabs" role="tablist">
            {SORTS.map(([key, label]) => (
              <button key={key} className={sort === key ? 'on' : ''} onClick={() => update({ sort: key === 'new' ? null : key })}>{label}</button>
            ))}
          </div>
          {data && <span className="label">{data.total} game{data.total === 1 ? '' : 's'}{filtered ? ' found' : ''}</span>}
        </div>

        {tags.length > 0 && (
          <div className="tags" style={{ paddingTop: 12 }}>
            {tags.map(t => (
              <button key={t.name} className={`tag${tag === t.name ? ' on' : ''}`} onClick={() => update({ tag: tag === t.name ? null : t.name })}>
                {t.name} <em>{t.count}</em>
              </button>
            ))}
          </div>
        )}

        {filtered && (
          <div className="row" style={{ paddingTop: 16 }}>
            {q && <span>Results for <b>“{q}”</b></span>}
            {tag && <span>tagged <b>{tag}</b></span>}
            <button className="link-btn" onClick={() => update({ q: null, tag: null })}>Clear filters</button>
          </div>
        )}

        {error ? <Empty title="Couldn't load games"><p className="muted">{error}</p></Empty>
          : !data ? <GridSkeleton />
          : data.items.length === 0 ? (
            filtered
              ? <Empty title="No games match"><p className="muted">Try a different search or tag.</p></Empty>
              : <Empty title="No games yet"><p className="muted">Be the first — build a game in the MyEngine editor and post it here.</p><Link className="btn primary" to="/new">Post a game</Link></Empty>
          ) : (
            <div className="grid">{data.items.map(g => <GameCardView key={g.slug} game={g} />)}</div>
          )}

        {pages > 1 && (
          <div className="row" style={{ justifyContent: 'center', paddingBottom: 64 }}>
            <button className="btn small" disabled={page <= 1} onClick={() => update({ page: String(page - 1) })}>← Prev</button>
            <span className="label">Page {page} / {pages}</span>
            <button className="btn small" disabled={page >= pages} onClick={() => update({ page: String(page + 1) })}>Next →</button>
          </div>
        )}
      </div>
    </>
  )
}
