import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { BookMarkedIcon, PlusIcon } from 'lucide-react'
import { toast } from 'sonner'

import type { LatestBacktestSummary, RuleTree, StrategyTemplate } from '@/api/labApi'
import {
  useCreateStrategyMutation,
  useGetStrategiesQuery,
  useGetStrategyTemplatesQuery,
} from '@/api/labApi'
import { getApiErrorMessage } from '@/api/types'
import { EmptyState } from '@/components/domain/EmptyState'
import { PageHeader } from '@/components/domain/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { StateBadge } from '@/features/lab/components/StateBadge'
import { defaultForm, formToRuleTree } from '@/features/lab/ruleTree'

function BacktestChip({ backtest }: { backtest?: LatestBacktestSummary | null }) {
  if (!backtest) return <span className="text-xs text-muted-foreground">no backtests</span>
  if (backtest.status !== 'done') {
    return <Badge variant="outline">backtest {backtest.status}</Badge>
  }
  return (
    <span className="flex flex-wrap items-center gap-1">
      {backtest.expectancyR != null && (
        <Badge variant={backtest.expectancyR > 0 ? 'success' : 'destructive'}>
          {backtest.expectancyR >= 0 ? '+' : ''}
          {backtest.expectancyR.toFixed(2)}R · n={backtest.trades ?? '?'}
        </Badge>
      )}
      {backtest.isExploratory && <Badge variant="destructive">exploratory</Badge>}
    </span>
  )
}

export default function StrategiesPage() {
  const navigate = useNavigate()
  const { data: strategies, isLoading } = useGetStrategiesQuery()
  const { data: templates = [] } = useGetStrategyTemplatesQuery()
  const [createStrategy, { isLoading: creating }] = useCreateStrategyMutation()

  const [open, setOpen] = useState(false)
  const [name, setName] = useState('')
  const [templateId, setTemplateId] = useState<string>('blank')

  const create = async () => {
    const template: StrategyTemplate | undefined = templates.find((t) => t.id === templateId)
    const ruleTree: RuleTree = template ? template.ruleTree : formToRuleTree(defaultForm())
    try {
      const created = await createStrategy({ name: name.trim(), ruleTree }).unwrap()
      toast.success('Strategy created')
      setOpen(false)
      navigate(`/lab/strategies/${created.id}`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create the strategy'))
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Strategies"
        description="Your playbook: named setups with explicit rules, versions and honest backtests."
        actions={
          <Button size="sm" onClick={() => setOpen(true)}>
            <PlusIcon className="size-4" aria-hidden />
            New strategy
          </Button>
        }
      />

      {isLoading && (
        <div className="flex flex-col gap-2">
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      )}

      {!isLoading && (strategies?.length ?? 0) === 0 && (
        <EmptyState
          icon={BookMarkedIcon}
          title="No strategies yet"
          hint="Codify each setup you trade — entry conditions, exit ladder, risk — then prove it through the promotion pipeline before real money touches it."
          action={
            <Button size="sm" onClick={() => setOpen(true)}>
              Create your first strategy
            </Button>
          }
        />
      )}

      <div className="flex flex-col gap-2">
        {strategies?.map((strategy) => (
          <Card key={strategy.id} className="py-3">
            <CardContent className="flex flex-wrap items-center justify-between gap-3 px-4">
              <div className="flex min-w-0 flex-col gap-1">
                <Link
                  to={`/lab/strategies/${strategy.id}`}
                  className="truncate font-medium hover:underline"
                >
                  {strategy.name}
                </Link>
                <span className="flex items-center gap-2 text-xs text-muted-foreground">
                  <StateBadge state={strategy.state} />
                  <span>v{strategy.currentVersion}</span>
                </span>
              </div>
              <BacktestChip backtest={strategy.latestBacktest} />
            </CardContent>
          </Card>
        ))}
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>New strategy</DialogTitle>
            <DialogDescription>
              Start from a playbook template or a blank rule tree. Everything is editable
              afterwards; each saved change becomes a new version.
            </DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-4">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="strategy-name">Name</Label>
              <Input
                id="strategy-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. BTC daily trend pullback"
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label>Template</Label>
              <div className="flex flex-col gap-1">
                <label className="flex items-start gap-2 rounded-lg border p-2 text-sm">
                  <input
                    type="radio"
                    name="template"
                    checked={templateId === 'blank'}
                    onChange={() => setTemplateId('blank')}
                    className="mt-1"
                  />
                  <span>
                    <span className="font-medium">Blank</span>
                    <span className="block text-xs text-muted-foreground">
                      One starter condition, 2×ATR stop.
                    </span>
                  </span>
                </label>
                {templates.map((template) => (
                  <label
                    key={template.id}
                    className="flex items-start gap-2 rounded-lg border p-2 text-sm"
                  >
                    <input
                      type="radio"
                      name="template"
                      checked={templateId === template.id}
                      onChange={() => setTemplateId(template.id)}
                      className="mt-1"
                    />
                    <span>
                      <span className="font-medium">{template.name}</span>
                      <span className="block text-xs text-muted-foreground">
                        {template.description}
                      </span>
                    </span>
                  </label>
                ))}
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button onClick={create} disabled={creating || name.trim() === ''}>
              Create
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
