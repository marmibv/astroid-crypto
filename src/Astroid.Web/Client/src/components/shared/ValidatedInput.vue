<template>
	<ValidationProvider
		v-slot="{ errors }"
		:rules="rules"
		:name="label"
		:vid="label"
	>
		<b-form-group :label="label" :description="description">
			<template v-slot:label>
				<slot name="label" />
			</template>
			<slot />
			<span class="text-danger" v-if="errors.length > 0">
				{{ errors[0] }}
			</span>
		</b-form-group>
	</ValidationProvider>
</template>
<script>
export default {
	props: {
		label: {
			type: String,
			required: true,
		},
		description: {
			type: String,
			required: false,
		},
		rules: {
			type: String,
			default: "required",
			required: false,
		},
	},
};
</script>
