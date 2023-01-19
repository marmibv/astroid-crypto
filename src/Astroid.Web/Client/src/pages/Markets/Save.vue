<template>
  <div>
    <page-header title="Save Market" :actions="actions" />
    <b-form-group label="Label">
      <b-form-input type="text" v-model="model.name" />
    </b-form-group>
    <b-form-group label="Description">
      <b-form-textarea v-model="model.description" rows="2" max-rows="6" />
    </b-form-group>
    <b-form-group label="Market">
      <v-select
        v-model="model.providerId"
        :options="providerOptions"
        placeholder="Select a market"
        @input="onSelect"
        v-if="!id"
      />
      <span v-else> {{ model.providerName }}</span>
    </b-form-group>
    <b-form-group
      :description="property.description"
      :label="property.displayName"
      v-for="property in this.model.properties"
      :key="property.property"
    >
      <v-dynamic-input :property="property" />
    </b-form-group>
  </div>
</template>

<script>
import Service from "../../services/markets";

export default {
  data() {
    return {
      actions: [
        {
          title: "Delete",
          event: () => this.delete(),
          icon: "fas fa-trash",
          variant: "light",
          hidden: () => !this.id,
        },
        {
          title: "Save",
          event: () => this.save(),
          icon: "fas fa-plus",
          variant: "primary",
        },
      ],
      model: {
        name: null,
        description: null,
        providerId: null,
        properties: [],
      },
      id: null,
      providers: [],
    };
  },
  computed: {
    providerOptions() {
      return this.providers.map((provider) => {
        return {
          id: provider.id,
          label: provider.title,
        };
      });
    },
  },
  async mounted() {
    this.id = this.$route.params.id;
    if (this.id) {
      const response = await Service.get(this.id);
      this.model = response.data.data;
    } else {
      await this.getMarketProviders();
    }
  },
  methods: {
    async getMarketProviders() {
      const response = await Service.getProviders();
      this.providers = response.data.data;
    },
    async save() {
      try {
        await Service.save(this.model);
        this.$router.push({ name: "market-list" });
      } catch (error) {
        console.error(error);
      }
    },
    delete() {
      this.$alert.remove(
        "Delete the Market?",
        "You won't be able to undo it",
        async () => {
          try {
            await Service.delete(this.id);
            this.$router.push({ name: "market-list" });
          } catch (error) {
            console.error(error);
          }
        }
      );
    },
    onSelect(id) {
      const provider = this.providers.find((x) => x.id === id);
      this.model.properties = Object.assign(
        this.model.properties,
        provider.properties
      );
    },
  },
};
</script>

<style></style>