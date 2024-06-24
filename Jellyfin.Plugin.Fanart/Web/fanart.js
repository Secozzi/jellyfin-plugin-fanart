const FanartConfig = {
    pluginUniqueId: '3a48e016-2720-4042-bb2a-deda48e1ceb2'
};

export default function (view) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(FanartConfig.pluginUniqueId).then(function (config) {
            page.querySelector('#apikey').value = config.PersonalApiKey || '';
            Dashboard.hideLoadingMsg();
        });
    });

    view.querySelector('#FanartConfigForm').addEventListener('submit', function (e) {
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(FanartConfig.pluginUniqueId).then(function (config) {
            config.PersonalApiKey = form.querySelector('#apikey').value;
            ApiClient.updatePluginConfiguration(FanartConfig.pluginUniqueId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });
        e.preventDefault();
        return false;
    });
}
