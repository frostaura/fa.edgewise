import { useState } from 'react'
import { AlertTriangleIcon } from 'lucide-react'
import { toast } from 'sonner'

import { useGetBucketsQuery, useUpdateBucketMutation, type Bucket } from '@/api/portfolioApi'
import { getApiErrorMessage } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { allocationWarning, parsePctInput } from '@/features/settings/bucketsLogic'
import { fractionToPct } from '@/features/settings/riskLogic'

const kindLabels: Record<Bucket['kind'], string> = {
  longTerm: 'Long-term',
  trading: 'Trading',
  prediction: 'Prediction',
  cash: 'Cash',
}

function BucketRow({ bucket }: { bucket: Bucket }) {
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(bucket.name)
  const [target, setTarget] = useState(String(fractionToPct(bucket.targetAllocPct)))
  const [split, setSplit] = useState(String(fractionToPct(bucket.contributionSplitPct)))
  const [error, setError] = useState<string | null>(null)
  const [updateBucket, updateState] = useUpdateBucketMutation()

  const startEdit = () => {
    setName(bucket.name)
    setTarget(String(fractionToPct(bucket.targetAllocPct)))
    setSplit(String(fractionToPct(bucket.contributionSplitPct)))
    setError(null)
    setEditing(true)
  }

  const save = async () => {
    const targetFraction = parsePctInput(target)
    const splitFraction = parsePctInput(split)
    if (!name.trim()) return setError('Name cannot be empty.')
    if (targetFraction === null) return setError('Target must be a percentage between 0 and 100.')
    if (splitFraction === null) return setError('Split must be a percentage between 0 and 100.')
    try {
      await updateBucket({
        id: bucket.id,
        patch: {
          name: name.trim(),
          targetAllocPct: targetFraction,
          contributionSplitPct: splitFraction,
        },
      }).unwrap()
      setEditing(false)
      setError(null)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save the bucket.'))
    }
  }

  if (!editing) {
    return (
      <TableRow>
        <TableCell className="font-medium">{bucket.name}</TableCell>
        <TableCell className="text-muted-foreground">{kindLabels[bucket.kind]}</TableCell>
        <TableCell className="text-right tabular-nums">{fractionToPct(bucket.targetAllocPct)}%</TableCell>
        <TableCell className="text-right tabular-nums">
          {fractionToPct(bucket.contributionSplitPct)}%
        </TableCell>
        <TableCell className="text-right">
          <Button size="sm" variant="ghost" onClick={startEdit}>
            Edit
          </Button>
        </TableCell>
      </TableRow>
    )
  }

  return (
    <TableRow>
      <TableCell>
        <Input
          aria-label={`${bucket.name} name`}
          value={name}
          onChange={(e) => setName(e.target.value)}
          className="h-8"
        />
      </TableCell>
      <TableCell className="text-muted-foreground">{kindLabels[bucket.kind]}</TableCell>
      <TableCell className="text-right">
        <Input
          aria-label={`${bucket.name} target allocation %`}
          type="number"
          inputMode="decimal"
          min={0}
          max={100}
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          className="ml-auto h-8 w-20 text-right"
        />
      </TableCell>
      <TableCell className="text-right">
        <Input
          aria-label={`${bucket.name} contribution split %`}
          type="number"
          inputMode="decimal"
          min={0}
          max={100}
          value={split}
          onChange={(e) => setSplit(e.target.value)}
          className="ml-auto h-8 w-20 text-right"
        />
      </TableCell>
      <TableCell className="text-right">
        <div className="flex justify-end gap-1">
          <Button size="sm" variant="ghost" onClick={() => setEditing(false)}>
            Cancel
          </Button>
          <Button size="sm" disabled={updateState.isLoading} onClick={() => void save()}>
            {updateState.isLoading ? 'Saving…' : 'Save'}
          </Button>
        </div>
        {error && (
          <p role="alert" className="mt-1 text-right text-xs text-destructive">
            {error}
          </p>
        )}
      </TableCell>
    </TableRow>
  )
}

export function BucketsSection() {
  const { data: buckets, isLoading, isError } = useGetBucketsQuery()

  if (isLoading) return <Skeleton className="h-64 w-full" />
  if (isError || !buckets) {
    return <p className="text-sm text-destructive">Could not load buckets. Try refreshing.</p>
  }

  const warnings = [
    allocationWarning(buckets.map((b) => b.targetAllocPct), 'Target allocations'),
    allocationWarning(buckets.map((b) => b.contributionSplitPct), 'Contribution splits'),
  ].filter((w): w is string => w !== null)

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-muted-foreground">
        Buckets split your capital by mandate. Target allocation drives drift flags on the
        portfolio page; the contribution split is how new deposits are suggested to be divided.
      </p>

      {warnings.map((warning) => (
        <p
          key={warning}
          role="status"
          className="flex items-center gap-2 rounded-md bg-warning/10 px-3 py-2 text-sm text-warning"
        >
          <AlertTriangleIcon className="size-4 shrink-0" aria-hidden />
          {warning}
        </p>
      ))}

      <div className="overflow-x-auto rounded-lg border border-border bg-surface">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Bucket</TableHead>
              <TableHead>Kind</TableHead>
              <TableHead className="text-right">Target alloc</TableHead>
              <TableHead className="text-right">Contribution split</TableHead>
              <TableHead className="w-36" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {buckets.map((bucket) => (
              <BucketRow key={bucket.id} bucket={bucket} />
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  )
}
