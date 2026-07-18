import { formatDistanceToNow } from 'date-fns'
import { NewspaperIcon } from 'lucide-react'

import { useGetNewsQuery } from '@/api/radarApi'
import { EmptyState } from '@/components/domain/EmptyState'
import { Skeleton } from '@/components/ui/skeleton'

/**
 * News digest for held + watched instruments. Defensive: GET /api/news is owned
 * by another vertical — the section degrades to a quiet empty state on error.
 */
export function NewsSection() {
  const { data: news, isLoading, isError } = useGetNewsQuery()

  if (isLoading) {
    return <Skeleton className="h-40 rounded-xl" />
  }

  if (isError || !news || news.length === 0) {
    return (
      <EmptyState
        icon={NewspaperIcon}
        title={isError ? 'News is not available yet' : 'No news for your instruments'}
        hint={
          isError
            ? 'The news feed has not come online in this deployment. Check back later.'
            : 'Headlines for held and watched instruments will appear here.'
        }
      />
    )
  }

  // Dedupe by title+source for display (feeds occasionally double-post).
  const seen = new Set<string>()
  const deduped = news.filter((n) => {
    const key = `${n.source}|${n.title}`.toLowerCase()
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })

  return (
    <ul className="flex flex-col divide-y rounded-lg border">
      {deduped.map((item) => (
        <li key={item.id} className="flex flex-col gap-0.5 px-3 py-2">
          <a
            href={item.url}
            target="_blank"
            rel="noreferrer noopener"
            className="text-sm font-medium hover:underline"
          >
            {item.title}
          </a>
          {item.summary && (
            <p className="line-clamp-2 text-xs text-muted-foreground">{item.summary}</p>
          )}
          <span className="text-xs text-muted-foreground">
            {item.source} · {formatDistanceToNow(new Date(item.publishedAt), { addSuffix: true })}
          </span>
        </li>
      ))}
    </ul>
  )
}
