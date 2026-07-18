import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { Link, Navigate, useLocation, useNavigate } from 'react-router'
import { Loader2Icon } from 'lucide-react'
import { z } from 'zod'

import { useLoginMutation } from '@/api/authApi'
import { getApiErrorMessage } from '@/api/types'
import { useAppSelector } from '@/app/hooks'
import { selectIsAuthenticated } from '@/features/auth/authSlice'
import { AuthCard, AuthError } from '@/features/auth/AuthCard'
import { Button } from '@/components/ui/button'
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from '@/components/ui/form'
import { Input } from '@/components/ui/input'

const schema = z.object({
  email: z.email('Enter a valid email address'),
  password: z.string().min(1, 'Password is required'),
})

type FormValues = z.infer<typeof schema>

export default function LoginPage() {
  const isAuthenticated = useAppSelector(selectIsAuthenticated)
  const location = useLocation()
  const navigate = useNavigate()
  const [login, { isLoading }] = useLoginMutation()
  const [apiError, setApiError] = useState<string | null>(null)

  const from = (location.state as { from?: string } | null)?.from ?? '/'

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: '', password: '' },
  })

  if (isAuthenticated) return <Navigate to={from} replace />

  const onSubmit = async (values: FormValues) => {
    setApiError(null)
    try {
      const result = await login(values).unwrap()
      if (result.requiresTotp) {
        void navigate('/totp', { state: { from } })
      } else {
        void navigate(from, { replace: true })
      }
    } catch (err) {
      setApiError(getApiErrorMessage(err, 'Sign in failed. Check your credentials.'))
    }
  }

  return (
    <AuthCard
      title="Welcome back"
      description="Sign in to your Edgewise account."
      footer={
        <span>
          New here?{' '}
          <Link to="/register" className="font-medium text-primary underline-offset-4 hover:underline">
            Create an account
          </Link>
        </span>
      }
    >
      <Form {...form}>
        <form onSubmit={form.handleSubmit(onSubmit)} className="flex flex-col gap-4" noValidate>
          <AuthError message={apiError} />
          <FormField
            control={form.control}
            name="email"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Email</FormLabel>
                <FormControl>
                  <Input
                    type="email"
                    autoComplete="email"
                    placeholder="you@example.com"
                    autoFocus
                    {...field}
                  />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="password"
            render={({ field }) => (
              <FormItem>
                <FormLabel>Password</FormLabel>
                <FormControl>
                  <Input type="password" autoComplete="current-password" {...field} />
                </FormControl>
                <FormMessage />
              </FormItem>
            )}
          />
          <Button type="submit" className="mt-2 w-full" disabled={isLoading}>
            {isLoading && <Loader2Icon className="animate-spin" aria-hidden />}
            Sign in
          </Button>
        </form>
      </Form>
    </AuthCard>
  )
}
