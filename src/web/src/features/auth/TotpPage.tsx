import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Link, Navigate, useLocation, useNavigate } from 'react-router'
import { Loader2Icon } from 'lucide-react'
import { z } from 'zod'

import { useLoginTotpMutation } from '@/api/authApi'
import { getApiErrorMessage } from '@/api/types'
import { useAppSelector } from '@/app/hooks'
import { selectIsAuthenticated, selectTotpToken } from '@/features/auth/authSlice'
import { AuthCard, AuthError } from '@/features/auth/AuthCard'
import { Button } from '@/components/ui/button'
import {
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form'
import { Input } from '@/components/ui/input'

const schema = z.object({
  code: z.string().regex(/^\d{6}$/, 'Enter the 6-digit code from your authenticator app'),
})

type FormValues = z.infer<typeof schema>

export default function TotpPage() {
  const isAuthenticated = useAppSelector(selectIsAuthenticated)
  const totpToken = useAppSelector(selectTotpToken)
  const location = useLocation()
  const navigate = useNavigate()
  const [loginTotp, { isLoading }] = useLoginTotpMutation()
  const [apiError, setApiError] = useState<string | null>(null)

  const from = (location.state as { from?: string } | null)?.from ?? '/'

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { code: '' },
  })

  if (isAuthenticated) return <Navigate to={from} replace />
  // No pending TOTP challenge — start over at the login screen.
  if (!totpToken) return <Navigate to="/login" replace />

  const onSubmit = async (values: FormValues) => {
    setApiError(null)
    try {
      await loginTotp({ totpToken, code: values.code }).unwrap()
      void navigate(from, { replace: true })
    } catch (err) {
      setApiError(getApiErrorMessage(err, 'That code did not work. Try again.'))
    }
  }

  return (
    <AuthCard
      title="Two-factor authentication"
      description="Enter the 6-digit code from your authenticator app."
      footer={
        <Link to="/login" className="font-medium text-primary underline-offset-4 hover:underline">
          Back to sign in
        </Link>
      }
    >
      <Form {...form}>
        <form onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4" noValidate>
          <AuthError message={apiError} />
          <FormField
            control={form.control}
            name="code"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Verification code</FormLabel>
                <FormControl>
                  <Input
                    inputMode="numeric"
                    autoComplete="one-time-code"
                    maxLength={6}
                    placeholder="123456"
                    autoFocus
                    className="text-center font-mono text-lg tracking-widest"
                    {...field}
                  />
                </FormControl>
                <FormDescription>Codes rotate every 30 seconds.</FormDescription>
                <FormMessage />
              </FormItem>
            )}
          />
          <Button type="submit" className="mt-2 w-full" disabled={isLoading}>
            {isLoading && <Loader2Icon className="animate-spin" aria-hidden />}
            Verify
          </Button>
        </form>
      </Form>
    </AuthCard>
  )
}
