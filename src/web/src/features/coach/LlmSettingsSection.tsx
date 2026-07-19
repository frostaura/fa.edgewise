import { SparklesIcon } from 'lucide-react'

import { useGetCoachStatusQuery } from '@/api/coachApi'
import { edgewiseApi } from '@/api/edgewiseApi'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { cn } from '@/lib/utils'

/** PATCH /api/me — only the llmOptOut flag; kept local to avoid owning the whole me API. */
const llmSettingsApi = edgewiseApi.injectEndpoints({
  endpoints: (build) => ({
    setLlmOptOut: build.mutation<unknown, boolean>({
      query: (llmOptOut) => ({ url: '/me/', method: 'PATCH', body: { llmOptOut } }),
    }),
  }),
})

function formatTokens(tokens: number): string {
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1)}M`
  if (tokens >= 1_000) return `${(tokens / 1_000).toFixed(0)}k`
  return `${tokens}`
}

/**
 * Settings → LLM: coach status, monthly token budget usage bar, and the opt-out switch.
 * Exported for the settings integrator to mount at /settings/llm.
 */
export function LlmSettingsSection() {
  const { data: status, isLoading, refetch } = useGetCoachStatusQuery()
  const [setLlmOptOut, { isLoading: saving }] = llmSettingsApi.useSetLlmOptOutMutation()

  if (isLoading || !status) {
    return <Skeleton className="h-48 w-full" />
  }

  const usedPct =
    status.budgetTokens > 0
      ? Math.min(100, Math.round((status.budgetUsedTokens / status.budgetTokens) * 100))
      : 0

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <SparklesIcon className="size-4" aria-hidden />
            Coach LLM
          </CardTitle>
          <CardDescription>
            {status.enabled
              ? `Powered by ${status.provider}. When the coach is unavailable you always keep deterministic, rules-based insights.`
              : 'No LLM key is configured on this server — the coach runs in deterministic, rules-based mode.'}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <div className="flex flex-col gap-1.5">
            <div className="flex items-baseline justify-between text-sm">
              <span className="font-medium">Monthly token budget</span>
              <span className="text-muted-foreground tabular-nums">
                {formatTokens(status.budgetUsedTokens)} / {formatTokens(status.budgetTokens)} tokens
              </span>
            </div>
            <div
              className="h-2 w-full overflow-hidden rounded-full bg-muted"
              role="progressbar"
              aria-valuenow={usedPct}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-label="Token budget used"
            >
              <div
                className={cn(
                  'h-full rounded-full transition-all',
                  usedPct >= 100 ? 'bg-destructive' : usedPct >= 80 ? 'bg-warning' : 'bg-primary',
                )}
                style={{ width: `${usedPct}%` }}
              />
            </div>
            {usedPct >= 100 && (
              <p className="text-xs text-destructive">
                Budget exhausted — the coach falls back to deterministic insights until next month.
              </p>
            )}
          </div>

          <div className="flex items-center justify-between gap-4">
            <div className="flex flex-col gap-0.5">
              <Label htmlFor="llm-opt-out">Opt out of LLM features</Label>
              <p className="text-xs text-muted-foreground">
                Nothing leaves the server: no post-mortems, weekly packs or chat via the LLM.
                Deterministic analytics keep running.
              </p>
            </div>
            <Switch
              id="llm-opt-out"
              checked={status.optedOut}
              disabled={saving}
              onCheckedChange={async (checked) => {
                await setLlmOptOut(checked)
                void refetch()
              }}
            />
          </div>
        </CardContent>
      </Card>
    </div>
  )
}

export default LlmSettingsSection
