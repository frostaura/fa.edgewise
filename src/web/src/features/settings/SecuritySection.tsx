import { useReducer, useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { CopyIcon, KeyRoundIcon, ShieldCheckIcon, ShieldOffIcon } from 'lucide-react'
import { QRCodeSVG } from 'qrcode.react'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'

import { useLogoutMutation } from '@/api/authApi'
import {
  useChangePasswordMutation,
  useCreateApiTokenMutation,
  useDeleteApiTokenMutation,
  useGetApiTokensQuery,
  useGetMeQuery,
  useTotpDisableMutation,
  useTotpEnableMutation,
  useTotpSetupMutation,
  type CreatedApiToken,
} from '@/api/settingsApi'
import { getApiErrorMessage } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import {
  changePasswordSchema,
  initialTotpFlow,
  totpReducer,
  type ChangePasswordForm,
} from '@/features/settings/securityLogic'

async function copyToClipboard(text: string, what: string) {
  try {
    await navigator.clipboard.writeText(text)
    toast.success(`${what} copied to clipboard.`)
  } catch {
    toast.error('Clipboard unavailable — copy manually.')
  }
}

// ----------------------------------------------------------- password card

function PasswordCard() {
  const [changePassword] = useChangePasswordMutation()
  const [logout] = useLogoutMutation()
  const navigate = useNavigate()

  const form = useForm<ChangePasswordForm>({
    resolver: zodResolver(changePasswordSchema),
    defaultValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
  })

  const submit = form.handleSubmit(async (values) => {
    try {
      await changePassword({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      }).unwrap()
      toast.success('Password changed. All sessions were signed out — log in again.')
      await logout().unwrap().catch(() => undefined)
      void navigate('/login')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not change your password.'))
    }
  })

  const field = (
    name: keyof ChangePasswordForm,
    label: string,
    autoComplete: string,
  ) => (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={`pw-${name}`}>{label}</Label>
      <Input
        id={`pw-${name}`}
        type="password"
        autoComplete={autoComplete}
        aria-invalid={!!form.formState.errors[name]}
        {...form.register(name)}
      />
      {form.formState.errors[name] && (
        <p role="alert" className="text-xs text-destructive">
          {form.formState.errors[name]?.message}
        </p>
      )}
    </div>
  )

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <KeyRoundIcon className="size-4" aria-hidden /> Password
        </CardTitle>
        <CardDescription>
          Changing your password signs out every device, including this one.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form className="flex max-w-sm flex-col gap-3" onSubmit={(e) => void submit(e)} noValidate>
          {field('currentPassword', 'Current password', 'current-password')}
          {field('newPassword', 'New password', 'new-password')}
          {field('confirmPassword', 'Confirm new password', 'new-password')}
          <Button type="submit" disabled={form.formState.isSubmitting} className="self-start">
            {form.formState.isSubmitting ? 'Changing…' : 'Change password'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

// --------------------------------------------------------------- TOTP card

function TotpCard({ totpEnabled }: { totpEnabled: boolean }) {
  const [flow, dispatch] = useReducer(totpReducer, initialTotpFlow)
  const [setup] = useTotpSetupMutation()
  const [enable] = useTotpEnableMutation()
  const [disable] = useTotpDisableMutation()

  const beginSetup = async () => {
    dispatch({ type: 'begin-setup' })
    try {
      const result = await setup().unwrap()
      dispatch({ type: 'setup-ready', secret: result.secret, otpauthUri: result.otpauthUri })
    } catch (err) {
      dispatch({ type: 'failed', error: getApiErrorMessage(err, 'Could not start TOTP setup.') })
    }
  }

  const submitEnable = async () => {
    dispatch({ type: 'submit' })
    try {
      const result = await enable({ code: flow.code.trim() }).unwrap()
      dispatch({ type: 'enabled', recoveryCodes: result.recoveryCodes })
    } catch (err) {
      dispatch({ type: 'failed', error: getApiErrorMessage(err, 'That code was not accepted.') })
    }
  }

  const submitDisable = async () => {
    dispatch({ type: 'submit' })
    try {
      await disable({ code: flow.code.trim() }).unwrap()
      dispatch({ type: 'disabled' })
      toast.success('Two-factor authentication disabled.')
    } catch (err) {
      dispatch({ type: 'failed', error: getApiErrorMessage(err, 'That code was not accepted.') })
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          {totpEnabled ? (
            <ShieldCheckIcon className="size-4 text-success" aria-hidden />
          ) : (
            <ShieldOffIcon className="size-4" aria-hidden />
          )}
          Two-factor authentication
        </CardTitle>
        <CardDescription>
          {totpEnabled
            ? 'TOTP is enabled — logging in requires a code from your authenticator app.'
            : 'Add a time-based one-time code (Google Authenticator, 1Password, Aegis…) to logins.'}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {flow.error && (
          <p role="alert" className="rounded-md bg-destructive/10 px-3 py-2 text-sm text-destructive">
            {flow.error}
          </p>
        )}

        {/* ------------------------------------------------ not enrolled */}
        {!totpEnabled && flow.step === 'idle' && (
          <Button onClick={() => void beginSetup()} disabled={flow.busy} className="self-start">
            {flow.busy ? 'Preparing…' : 'Enable two-factor auth'}
          </Button>
        )}

        {!totpEnabled && flow.step === 'setup' && flow.otpauthUri && (
          <div className="flex flex-col gap-4 sm:flex-row sm:items-start">
            <div className="self-center rounded-md bg-white p-3 sm:self-auto">
              <QRCodeSVG value={flow.otpauthUri} size={160} aria-label="TOTP enrolment QR code" />
            </div>
            <div className="flex flex-1 flex-col gap-3">
              <p className="text-sm text-muted-foreground">
                Scan the QR code with your authenticator app, or enter the secret manually:
              </p>
              <div className="flex items-center gap-2">
                <code className="rounded bg-muted px-2 py-1 font-mono text-xs break-all">
                  {flow.secret}
                </code>
                <Button
                  size="sm"
                  variant="ghost"
                  aria-label="Copy secret"
                  onClick={() => void copyToClipboard(flow.secret ?? '', 'Secret')}
                >
                  <CopyIcon />
                </Button>
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="totp-code">6-digit code from the app</Label>
                <div className="flex gap-2">
                  <Input
                    id="totp-code"
                    inputMode="numeric"
                    autoComplete="one-time-code"
                    placeholder="123456"
                    className="w-32 tabular-nums"
                    value={flow.code}
                    onChange={(e) => dispatch({ type: 'code-input', code: e.target.value })}
                  />
                  <Button
                    disabled={flow.busy || flow.code.trim().length < 6}
                    onClick={() => void submitEnable()}
                  >
                    {flow.busy ? 'Verifying…' : 'Verify & enable'}
                  </Button>
                  <Button variant="ghost" onClick={() => dispatch({ type: 'cancel' })}>
                    Cancel
                  </Button>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* ------------------------------------------- recovery codes, once */}
        {flow.step === 'recovery' && flow.recoveryCodes && (
          <div className="flex flex-col gap-3" data-testid="recovery-codes">
            <p className="text-sm font-medium">
              Save these recovery codes now — they are shown exactly once and each works one time
              if you lose your authenticator.
            </p>
            <div className="grid max-w-sm grid-cols-2 gap-1 rounded-md bg-muted p-3 font-mono text-sm">
              {flow.recoveryCodes.map((code) => (
                <span key={code}>{code}</span>
              ))}
            </div>
            <div className="flex gap-2">
              <Button
                variant="outline"
                onClick={() => void copyToClipboard(flow.recoveryCodes!.join('\n'), 'Recovery codes')}
              >
                <CopyIcon /> Copy all
              </Button>
              <Button
                onClick={() => {
                  dispatch({ type: 'cancel' })
                  toast.success('Two-factor authentication enabled.')
                }}
              >
                I saved them
              </Button>
            </div>
          </div>
        )}

        {/* --------------------------------------------------- enrolled */}
        {totpEnabled && flow.step === 'idle' && (
          <Button variant="outline" onClick={() => dispatch({ type: 'begin-disable' })} className="self-start">
            Disable two-factor auth
          </Button>
        )}

        {totpEnabled && flow.step === 'disable' && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="totp-disable-code">Enter a TOTP or recovery code to confirm</Label>
            <div className="flex gap-2">
              <Input
                id="totp-disable-code"
                inputMode="numeric"
                autoComplete="one-time-code"
                className="w-40 tabular-nums"
                value={flow.code}
                onChange={(e) => dispatch({ type: 'code-input', code: e.target.value })}
              />
              <Button
                variant="destructive"
                disabled={flow.busy || !flow.code.trim()}
                onClick={() => void submitDisable()}
              >
                {flow.busy ? 'Disabling…' : 'Disable'}
              </Button>
              <Button variant="ghost" onClick={() => dispatch({ type: 'cancel' })}>
                Cancel
              </Button>
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ---------------------------------------------------------------- PAT card

function ApiTokensCard() {
  const { data: tokens, isLoading } = useGetApiTokensQuery()
  const [createToken, createState] = useCreateApiTokenMutation()
  const [deleteToken] = useDeleteApiTokenMutation()
  const [name, setName] = useState('')
  const [created, setCreated] = useState<CreatedApiToken | null>(null)

  const create = async () => {
    try {
      const result = await createToken({ name: name.trim() }).unwrap()
      setCreated(result)
      setName('')
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not create the token.'))
    }
  }

  const remove = async (id: string, tokenName: string) => {
    if (!window.confirm(`Delete "${tokenName}"? Anything using it stops working immediately.`)) return
    try {
      await deleteToken(id).unwrap()
      if (created?.id === id) setCreated(null)
    } catch (err) {
      toast.error(getApiErrorMessage(err, 'Could not delete the token.'))
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Personal access tokens</CardTitle>
        <CardDescription>
          For scripts and integrations — a token grants the same access as your login. Sent as
          a Bearer header.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {created && (
          <div className="flex flex-col gap-2 rounded-md border border-primary/40 bg-primary/5 p-3">
            <p className="text-sm font-medium">
              “{created.name}” created — copy the token now, it will not be shown again.
            </p>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 truncate rounded bg-muted px-2 py-1 font-mono text-xs">
                {created.token}
              </code>
              <Button
                size="sm"
                variant="outline"
                onClick={() => void copyToClipboard(created.token, 'Token')}
              >
                <CopyIcon /> Copy
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setCreated(null)}>
                Done
              </Button>
            </div>
          </div>
        )}

        {isLoading && <Skeleton className="h-16 w-full" />}
        {!isLoading && (tokens ?? []).length === 0 && (
          <p className="text-sm text-muted-foreground">No tokens yet.</p>
        )}
        {(tokens ?? []).map((token) => (
          <div key={token.id} className="flex items-center gap-3 rounded-md border border-border px-3 py-2">
            <div className="min-w-0 flex-1">
              <p className="truncate text-sm font-medium">{token.name}</p>
              <p className="text-xs text-muted-foreground">
                Created {new Date(token.createdAt).toLocaleDateString()} · last used{' '}
                {token.lastUsedAt ? new Date(token.lastUsedAt).toLocaleString() : 'never'}
              </p>
            </div>
            <Button
              size="sm"
              variant="ghost"
              className="text-destructive hover:text-destructive"
              onClick={() => void remove(token.id, token.name)}
            >
              Delete
            </Button>
          </div>
        ))}

        <form
          className="flex max-w-sm gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            if (name.trim()) void create()
          }}
        >
          <Input
            aria-label="New token name"
            placeholder="Token name (e.g. backup script)"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <Button type="submit" disabled={!name.trim() || createState.isLoading}>
            {createState.isLoading ? 'Creating…' : 'Create'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

// ---------------------------------------------------------------- section

export function SecuritySection() {
  const { data: me, isLoading } = useGetMeQuery()

  if (isLoading || !me) return <Skeleton className="h-64 w-full" />

  return (
    <div className="flex flex-col gap-4">
      <PasswordCard />
      <TotpCard totpEnabled={me.totpEnabled} />
      <ApiTokensCard />
    </div>
  )
}
