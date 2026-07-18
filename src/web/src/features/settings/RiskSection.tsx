import { useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { HistoryIcon, PlusIcon, ShieldCheckIcon } from 'lucide-react'
import { useForm, type UseFormReturn } from 'react-hook-form'
import { toast } from 'sonner'

import {
  useActivateRiskProfileMutation,
  useCreateRiskProfileMutation,
  useDeleteRiskProfileMutation,
  useGetRiskProfilesQuery,
  useUpdateRiskProfileMutation,
  type RiskProfile,
} from '@/api/riskProfilesApi'
import { getApiErrorMessage } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
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
import { Separator } from '@/components/ui/separator'
import { Skeleton } from '@/components/ui/skeleton'
import {
  formatPct,
  formToValues,
  profileToForm,
  riskPresets,
  riskProfileFormSchema,
  type RiskProfileForm,
} from '@/features/settings/riskLogic'

// --------------------------------------------------------------- stat grid

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="text-sm font-medium tabular-nums">{value}</span>
    </div>
  )
}

function ProfileCard({ profile }: { profile: RiskProfile }) {
  const [editing, setEditing] = useState(false)
  const [activate, activateState] = useActivateRiskProfileMutation()
  const [remove, removeState] = useDeleteRiskProfileMutation()
  const ladder = profile.ladderThresholds

  const onActivate = async () => {
    try {
      await activate(profile.id).unwrap()
      toast.success(`${profile.name} is now the active risk profile.`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not activate the profile.'))
    }
  }

  const onDelete = async () => {
    if (!window.confirm(`Delete "${profile.name}" and all of its versions? This cannot be undone.`)) return
    try {
      await remove(profile.id).unwrap()
      toast.success(`${profile.name} deleted.`)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not delete the profile.'))
    }
  }

  return (
    <Card className={profile.isActive ? 'border-primary' : undefined} data-testid={`risk-profile-${profile.name}`}>
      <CardHeader className="pb-3">
        <div className="flex items-center gap-2">
          <CardTitle className="text-base">{profile.name}</CardTitle>
          {profile.isActive && (
            <Badge>
              <ShieldCheckIcon className="size-3" aria-hidden /> Active
            </Badge>
          )}
          <span
            className="ml-auto inline-flex items-center gap-1 text-xs text-muted-foreground"
            title="Edits never overwrite history — each save creates a new version."
          >
            <HistoryIcon className="size-3" aria-hidden /> v{profile.version}
          </span>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="grid grid-cols-3 gap-x-4 gap-y-3">
          <Stat label="Risk / trade" value={formatPct(profile.riskPct)} />
          <Stat label="Heat cap" value={formatPct(profile.heatCapPct)} />
          <Stat label="Cluster cap" value={formatPct(profile.clusterCapPct)} />
          <Stat label="Daily stop" value={formatPct(profile.dailyStopPct)} />
          <Stat label="Loss-count stop" value={`${profile.dailyLossCountStop} losses`} />
          <Stat label="Weekly stop" value={formatPct(profile.weeklyStopPct)} />
          <Stat label="Max leverage" value={`${profile.maxLeverage}×`} />
          <Stat label="Min R:R" value={`${profile.minRR}`} />
          <Stat label="Max positions" value={`${profile.maxPositions}`} />
        </div>

        <Separator />
        <p className="text-xs text-muted-foreground">
          Drawdown ladder: risk halved at −{formatPct(ladder.riskHalvedPct)} · paused at −
          {formatPct(ladder.pausedPct)} · paper-only at −{formatPct(ladder.paperPct)}
        </p>

        <div className="flex gap-2">
          <Button size="sm" variant="outline" onClick={() => setEditing(true)}>
            Edit
          </Button>
          {!profile.isActive && (
            <>
              <Button size="sm" disabled={activateState.isLoading} onClick={() => void onActivate()}>
                Activate
              </Button>
              <Button
                size="sm"
                variant="ghost"
                className="ml-auto text-destructive hover:text-destructive"
                disabled={removeState.isLoading}
                onClick={() => void onDelete()}
              >
                Delete
              </Button>
            </>
          )}
        </div>
      </CardContent>
      {editing && <ProfileDialog profile={profile} onClose={() => setEditing(false)} />}
    </Card>
  )
}

// ----------------------------------------------------------- edit dialog

function PctField({
  form,
  name,
  label,
  step = 0.1,
}: {
  form: UseFormReturn<RiskProfileForm>
  name: keyof RiskProfileForm & string
  label: string
  step?: number
}) {
  const error = form.formState.errors[name]?.message
  return (
    <div className="flex flex-col gap-1">
      <Label htmlFor={`rp-${name}`} className="text-xs">
        {label}
      </Label>
      <Input
        id={`rp-${name}`}
        type="number"
        step={step}
        inputMode="decimal"
        aria-invalid={!!error}
        {...form.register(name, { valueAsNumber: true })}
      />
      {typeof error === 'string' && (
        <p role="alert" className="text-xs text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}

function ProfileDialog({ profile, onClose }: { profile: RiskProfile | null; onClose: () => void }) {
  const [create, createState] = useCreateRiskProfileMutation()
  const [update, updateState] = useUpdateRiskProfileMutation()
  const saving = createState.isLoading || updateState.isLoading

  const form = useForm<RiskProfileForm>({
    resolver: zodResolver(riskProfileFormSchema),
    defaultValues: profile ? profileToForm(profile) : { name: '', ...riskPresets.Standard },
  })

  const applyPreset = (preset: keyof typeof riskPresets) => {
    form.reset({ name: form.getValues('name') || preset, ...riskPresets[preset] })
  }

  const submit = form.handleSubmit(async (values) => {
    try {
      if (profile) {
        await update({ id: profile.id, values: formToValues(values) }).unwrap()
        toast.success(`${profile.name} saved as version ${profile.version + 1}.`)
      } else {
        await create({ name: values.name, ...formToValues(values) }).unwrap()
        toast.success(`${values.name} created.`)
      }
      onClose()
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not save the risk profile.'))
    }
  })

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{profile ? `Edit ${profile.name}` : 'New risk profile'}</DialogTitle>
          <DialogDescription>
            {profile
              ? `Saving creates version ${profile.version + 1}; every engine picks it up immediately.`
              : 'Start from a preset, then tune the guardrails. Percentages are of bucket equity.'}
          </DialogDescription>
        </DialogHeader>

        {!profile && (
          <div className="flex items-center gap-2">
            <span className="text-xs text-muted-foreground">Preset:</span>
            {(Object.keys(riskPresets) as (keyof typeof riskPresets)[]).map((preset) => (
              <Button key={preset} type="button" size="sm" variant="outline" onClick={() => applyPreset(preset)}>
                {preset}
              </Button>
            ))}
          </div>
        )}

        <form className="flex flex-col gap-4" onSubmit={(e) => void submit(e)} noValidate>
          {!profile && (
            <div className="flex flex-col gap-1">
              <Label htmlFor="rp-name" className="text-xs">
                Name
              </Label>
              <Input id="rp-name" aria-invalid={!!form.formState.errors.name} {...form.register('name')} />
              {form.formState.errors.name && (
                <p role="alert" className="text-xs text-destructive">
                  {form.formState.errors.name.message}
                </p>
              )}
            </div>
          )}

          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3">
            <PctField form={form} name="riskPct" label="Risk per trade (%)" step={0.05} />
            <PctField form={form} name="heatCapPct" label="Heat cap (%)" />
            <PctField form={form} name="clusterCapPct" label="Cluster cap (%)" />
            <PctField form={form} name="dailyStopPct" label="Daily stop (%)" />
            <PctField form={form} name="weeklyStopPct" label="Weekly stop (%)" />
            <PctField form={form} name="dailyLossCountStop" label="Daily loss count" step={1} />
            <PctField form={form} name="maxLeverage" label="Max leverage (×)" step={0.5} />
            <PctField form={form} name="minRR" label="Min R:R" />
            <PctField form={form} name="maxPositions" label="Max positions" step={1} />
          </div>

          <div className="flex flex-col gap-2 rounded-md border border-border p-3">
            <p className="text-xs font-medium">Drawdown ladder (from the trading bucket's high-water mark)</p>
            <div className="grid grid-cols-3 gap-3">
              <PctField form={form} name="riskHalvedPct" label="Risk halved at (%)" />
              <PctField form={form} name="pausedPct" label="Paused at (%)" />
              <PctField form={form} name="paperPct" label="Paper-only at (%)" />
            </div>
          </div>

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={saving}>
              {saving ? 'Saving…' : profile ? `Save version ${profile.version + 1}` : 'Create profile'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

// --------------------------------------------------------------- section

export function RiskSection() {
  const { data: profiles, isLoading, isError } = useGetRiskProfilesQuery()
  const [creating, setCreating] = useState(false)

  if (isLoading) return <Skeleton className="h-64 w-full" />
  if (isError || !profiles) {
    return <p className="text-sm text-destructive">Could not load risk profiles. Try refreshing.</p>
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          The active profile drives position sizing, circuit breakers, adherence grading and the
          drawdown ladder. Edits are versioned — old plans keep the rules they were graded under.
        </p>
        <Button size="sm" onClick={() => setCreating(true)}>
          <PlusIcon /> New profile
        </Button>
      </div>

      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        {profiles.map((profile) => (
          <ProfileCard key={profile.id} profile={profile} />
        ))}
      </div>

      {creating && <ProfileDialog profile={null} onClose={() => setCreating(false)} />}
    </div>
  )
}
