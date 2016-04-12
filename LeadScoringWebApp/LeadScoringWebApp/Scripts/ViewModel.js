function ViewModel() {
    var self = this;
    this.inputText = ko.observable().extend({ rateLimit: { method: "notifyWhenChangesStop", timeout: 400 } });;
    this.output = ko.pureComputed(function() {
        if (self.inputText()) {
            return $.getJSON("/Home/AnalyzeJson", { inputText: self.inputText() }).then(function (data) {
                return new SentimentModel(data);
            });
        }
    }).extend({async: null});

    // button
    this.analyze = function () {
        self.processing(true);
        $.getJSON("/Home/AnalyzeJson", { inputText: self.inputText() }).done(function (data) {
            self.output({ KeyPhrases: data.Item1, Sentiment: data.Item2 });
            self.processing(false);
        });
    }
}

function SentimentModel(data) {
    var self = this;
    this.KeyPhrases = data.Item1;
    this.Sentiment = data.Item2;

    this.sentimentClass = ko.pureComputed(function () {
        if (self.Sentiment < .4)
            return "progress-bar-danger";
        else if (self.Sentiment > .6)
            return "progress-bar-success";
        return "progress-bar-warning";
    });

    this.sentimentValue = ko.pureComputed(function () {
        return Math.round(self.Sentiment * 100);
    });

}


var vm = new ViewModel();

ko.applyBindings(vm);


function ProgressBarIntervalHandler() {
    var progressBarStr = document.getElementById("ProgressBar").innerHTML;
    var substrings = progressBarStr.split(".");
    if (substrings.length === 6) {
        progressBarStr = ". ";
    } else {
        progressBarStr += ". ";
    }
    document.getElementById("ProgressBar").innerHTML = progressBarStr;
}

function UpdateOnBegin() {
    document.getElementById("AnalysisResults").innerHTML = "";
    document.getElementById("AnalysisResults").style.display = "none";
    document.getElementById("Analyze").disabled = "disabled";
}

function UpdateOnSuccess() {
    document.getElementById("Analyze").disabled = "";
    document.getElementById("AnalysisResults").style.display = "block";
}

function UpdateOnError(ajaxContext) {
    document.getElementById("AnalysisResults").innerHTML = "Unfortunately your request errored out [HTTP status code " + ajaxContext.status +
        "]. Please retry your request";
    document.getElementById("AnalysisResults").style.display = "block";
    document.getElementById("Analyze").disabled = "";
}
