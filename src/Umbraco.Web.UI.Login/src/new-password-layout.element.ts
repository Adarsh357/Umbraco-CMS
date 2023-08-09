import type { UUIButtonState, UUIInputPasswordElement } from '@umbraco-ui/uui';
import { UUITextStyles } from '@umbraco-ui/uui-css';
import { CSSResultGroup, LitElement, css, html } from 'lit';
import { customElement, property, query } from 'lit/decorators.js';

@customElement('umb-new-password-layout')
export default class UmbNewPasswordLayoutElement extends LitElement {
	@query('#confirmPassword')
	confirmPasswordElement!: UUIInputPasswordElement;

	@property()
	state: UUIButtonState = undefined;

	#onSubmit(event: Event) {
		event.preventDefault();
		const form = event.target as HTMLFormElement;

		this.confirmPasswordElement.setCustomValidity('');

		if (!form) return;
		if (!form.checkValidity()) return;

		const formData = new FormData(form);
		const password = formData.get('password') as string;
		const passwordConfirm = formData.get('confirmPassword') as string;

		if (password !== passwordConfirm) {
			this.confirmPasswordElement.setCustomValidity('Passwords do not match');
			return;
		}

		this.dispatchEvent(new CustomEvent('submit', { detail: { password } }));
	}

	render() {
		return html`
			<uui-form>
				<form id="LoginForm" name="login" @submit=${this.#onSubmit}>
					<div id="header">
						<h2>Create new password</h2>
						<span> Enter a new password for your account. </span>
					</div>
					<uui-form-layout-item>
						<uui-label id="passwordLabel" for="password" slot="label" required>Password</uui-label>
						<uui-input-password
							type="password"
							id="password"
							name="password"
							label="Password"
							required
							required-message="Password is required"></uui-input-password>
					</uui-form-layout-item>

					<uui-form-layout-item>
						<uui-label id="confirmPasswordLabel" for="confirmPassword" slot="label" required>
							Confirm password
						</uui-label>
						<uui-input-password
							type="password"
							id="confirmPassword"
							name="confirmPassword"
							label="ConfirmPassword"
							required
							required-message="ConfirmPassword is required"></uui-input-password>
					</uui-form-layout-item>

					<uui-button type="submit" label="Continue" look="primary" color="default" .state=${this.state}></uui-button>
				</form>
			</uui-form>

			<umb-back-to-login-button style="margin-top: var(--uui-size-space-6)"></umb-back-to-login-button>
		`;
	}

	static styles: CSSResultGroup = [
		UUITextStyles,
		css`
			#header {
				text-align: center;
				display: flex;
				flex-direction: column;
				gap: var(--uui-size-space-5);
			}
			#header span {
				color: #868686; /* TODO Change to uui color when uui gets a muted text variable */
				font-size: 14px;
			}
			#header h2 {
				margin: 0px;
				font-weight: bold;
				font-size: 1.4rem;
			}
			form {
				display: flex;
				flex-direction: column;
				gap: var(--uui-size-space-5);
			}
			uui-form-layout-item {
				margin: 0;
			}
			h2 {
				margin: 0px;
				font-weight: 600;
				font-size: 1.4rem;
				margin-bottom: var(--uui-size-space-4);
			}
			uui-input-password {
				width: 100%;
			}
			uui-button {
				width: 100%;
				margin-top: var(--uui-size-space-5);
				--uui-button-padding-top-factor: 1.5;
				--uui-button-padding-bottom-factor: 1.5;
			}
		`,
	];
}

declare global {
	interface HTMLElementTagNameMap {
		'umb-new-password-layout': UmbNewPasswordLayoutElement;
	}
}
